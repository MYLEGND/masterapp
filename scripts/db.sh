#!/usr/bin/env bash
# MASTERAPP's self-service EF Core workflow. This command is intentionally
# local-only: it never reads production connection-string variables, deploys an
# application, or executes a migration bundle.

set -Eeuo pipefail
IFS=$'\n\t'

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$ROOT_DIR"

readonly EF_PROJECT="Infrastructure/Infrastructure.csproj"
readonly STARTUP_PROJECT="AgentPortal/AgentPortal.csproj"
readonly DB_CONTEXT="MasterAppDbContext"
readonly TEST_PROJECT="AgentPortal.Tests/AgentPortal.Tests.csproj"
readonly MIGRATION_DIR="Infrastructure/Migrations"
readonly SNAPSHOT_FILE="$MIGRATION_DIR/MasterAppDbContextModelSnapshot.cs"
readonly TOOL_MANIFEST="$ROOT_DIR/.config/dotnet-tools.json"
readonly LEGACY_MIGRATIONS="$ROOT_DIR/scripts/db-legacy-manual-migrations.txt"
readonly BUNDLE_PATH="$ROOT_DIR/artifacts/migrations/masterapp-migrate"

WORK_DIR="$(mktemp -d "${TMPDIR:-/tmp}/masterapp-db.XXXXXX")"
CHANGED_PATHS_FILE="$WORK_DIR/changed-paths.txt"
trap 'rm -rf "$WORK_DIR"' EXIT

MODEL_STATUS=""
INTEGRITY_ERRORS=()
MANUAL_REVIEW_FINDINGS=()
PENDING_MIGRATION_FILES=()

usage() {
    cat <<'EOF'
Usage: ./scripts/db.sh <command> [MigrationName]

Commands:
  check                 Build, test, and inspect the EF model without changing files.
  sync <MigrationName>  Generate and safely apply one local migration when required.
  apply                 Apply already-generated, validated migrations to the local database.
  validate              Run the complete pre-commit migration validation.
  bundle                Validate, then create artifacts/migrations/masterapp-migrate.

Optional local database override:
  MASTERAPP_LOCAL_DB_CONNECTION='Data Source=/absolute/path/masterapp.db' ./scripts/db.sh apply

The override is deliberately local-only. Known Azure production hosts are refused,
and connection strings are never printed.
EOF
}

result() {
    printf '\n[RESULT] %s\n' "$1"
}

build_or_test_failure() {
    printf '[ERROR] %s\n' "$1" >&2
    printf '[RECOVERY] Resolve the build or test failure, then run ./scripts/db.sh check\n' >&2
    result "BUILD OR TEST FAILURE"
    exit 30
}

artifact_failure() {
    printf '[ERROR] %s\n' "$1" >&2
    printf '[RECOVERY] Fix the reported migration artifacts, then run ./scripts/db.sh validate\n' >&2
    result "MIGRATION ARTIFACTS INVALID"
    exit 20
}

ensure_ef_tool() {
    printf '\n[EF] Restoring the repository-local dotnet-ef tool\n'
    if ! dotnet tool restore --tool-manifest "$TOOL_MANIFEST" --verbosity quiet; then
        artifact_failure "The repository-local dotnet-ef tool could not be restored."
    fi
}

is_known_production_connection() {
    local value="$1"
    printf '%s' "$value" | grep -Eqi 'database\.windows\.net|masterapp-sql-prod|portal\.mylegnd\.com'
}

is_local_connection() {
    local value="$1"
    # Alternate connections are intentionally limited to SQLite files or a
    # loopback database server. This prevents an environment variable from
    # turning a local developer command into a remote deployment command.
    printf '%s' "$value" | grep -Eqi '(^|;)[[:space:]]*(data[[:space:]]+source|filename)[[:space:]]*=[[:space:]]*(/|\.{1,2}/|:memory:|[^;]*\.(db|sqlite)(;|$))|(^|;)[[:space:]]*(server|data[[:space:]]+source)[[:space:]]*=[[:space:]]*(localhost|127\.0\.0\.1|\[::1\])([,;]|$)'
}

assert_local_connection_override() {
    [[ -n "${MASTERAPP_LOCAL_DB_CONNECTION:-}" ]] || return 0

    if is_known_production_connection "$MASTERAPP_LOCAL_DB_CONNECTION"; then
        printf '[LOCAL DATABASE] Refusing a known production connection string.\n' >&2
        return 64
    fi

    if ! is_local_connection "$MASTERAPP_LOCAL_DB_CONNECTION"; then
        printf '[LOCAL DATABASE] Refusing a non-local alternate connection string.\n' >&2
        return 64
    fi
}

run_ef() (
    # Do not let a developer's shell or CI inherit an App Service connection.
    unset SQLCONNSTR_MasterAppDb MasterAppDb ConnectionStrings__MasterAppDb
    unset WEBSITE_SITE_NAME WEBSITE_HOSTNAME WEBSITE_INSTANCE_ID
    export ASPNETCORE_ENVIRONMENT=Development
    export DOTNET_ENVIRONMENT=Development

    assert_local_connection_override || exit $?

    if [[ -n "${MASTERAPP_LOCAL_DB_CONNECTION:-}" ]]; then
        export ConnectionStrings__MasterAppDb="$MASTERAPP_LOCAL_DB_CONNECTION"
        export MASTERAPP_DEV_USE_SQLSERVER=true
        printf '[LOCAL DATABASE] Using caller-provided local database configuration.\n'
    else
        # EF model validation must use the same relational provider as the
        # canonical migration snapshot. A loopback-only design-time SQL Server
        # connection provides provider metadata without introducing a remote
        # database, production credential, or committed secret.
        #
        # Commands such as migrations has-pending-model-changes compare model
        # metadata and do not require this endpoint to host a live database.
        export ConnectionStrings__MasterAppDb="Server=127.0.0.1;Database=masterapp_ef_design_time;Integrated Security=true;TrustServerCertificate=true;Encrypt=false"
        export EF_FORCE_SQLSERVER=true
        export MASTERAPP_DEV_USE_SQLSERVER=true
        printf '[LOCAL DATABASE] Using isolated loopback SQL Server design-time configuration.\n'
    fi

    dotnet tool run dotnet-ef "$@"
)

case "${1:-}" in
    check|sync|apply|validate|bundle)
        assert_local_connection_override || exit $?
        ;;
esac

build_backend() {
    printf '\n[BUILD] %s\n' "$STARTUP_PROJECT"
    if ! dotnet build "$STARTUP_PROJECT" --nologo; then
        build_or_test_failure "The backend build failed."
    fi
}

test_backend() {
    printf '\n[TEST] %s\n' "$TEST_PROJECT"
    if ! dotnet test "$TEST_PROJECT" --nologo; then
        build_or_test_failure "The backend test suite failed."
    fi
}

check_model() {
    local output="$WORK_DIR/model-check.log"
    printf '\n[MODEL] Checking for EF pending model changes\n'

    if run_ef migrations has-pending-model-changes \
        --project "$EF_PROJECT" \
        --startup-project "$STARTUP_PROJECT" \
        --context "$DB_CONTEXT" \
        --no-build >"$output" 2>&1; then
        cat "$output"
        MODEL_STATUS="clean"
        return 0
    fi

    cat "$output" >&2
    if grep -Eqi 'pending model changes|changes have been made to the model' "$output"; then
        MODEL_STATUS="required"
        return 0
    fi

    artifact_failure "EF could not determine whether the model has pending changes."
}

is_legacy_manual_migration() {
    local path="$1"
    local relative_path="${path#"$MIGRATION_DIR"/}"
    grep -Fqx "$relative_path" "$LEGACY_MIGRATIONS"
}

is_temporary_migration_name() {
    local file_name="$1"
    local name="${file_name%.cs}"
    name="${name#*_}"
    local lower
    lower="$(printf '%s' "$name" | tr '[:upper:]' '[:lower:]')"

    case "$lower" in
        fix|update|migration|changes|test|temp|temporary|wip|scratch|testmigration)
            return 0
            ;;
        *temporary*|*scratch*|*testmigration*|*wip*)
            return 0
            ;;
    esac
    return 1
}

refresh_changed_paths() {
    : >"$CHANGED_PATHS_FILE"

    # CI supplies DB_BASE_REF so migrations committed in a push or PR are
    # checked as a complete artifact set. Local use checks the working tree.
    if [[ -n "${DB_BASE_REF:-}" ]] && git cat-file -e "${DB_BASE_REF}^{commit}" 2>/dev/null; then
        git diff --name-only "${DB_BASE_REF}...HEAD" >>"$CHANGED_PATHS_FILE"
    fi

    git diff --name-only HEAD >>"$CHANGED_PATHS_FILE"
    git diff --cached --name-only >>"$CHANGED_PATHS_FILE"
    git ls-files --others --exclude-standard >>"$CHANGED_PATHS_FILE"
    sort -u "$CHANGED_PATHS_FILE" -o "$CHANGED_PATHS_FILE"
}

path_changed() {
    grep -Fqx "$1" "$CHANGED_PATHS_FILE"
}

artifact_base_ref() {
    if [[ -n "${DB_BASE_REF:-}" ]] && git cat-file -e "${DB_BASE_REF}^{commit}" 2>/dev/null; then
        printf '%s' "$DB_BASE_REF"
        return 0
    fi

    if git rev-parse --verify HEAD >/dev/null 2>&1; then
        printf 'HEAD'
        return 0
    fi

    return 1
}

is_historical_designer_restoration() {
    local designer_path="$1"
    local source_path="$2"
    local prior_ref=""

    prior_ref="$(artifact_base_ref)" || return 1

    # This exception is deliberately narrow:
    # - the migration source must already exist in the prior committed state;
    # - the designer must NOT exist in that prior state;
    # - both source and reconstructed designer must exist now;
    # - the designer must contain the historical target model.
    #
    # Therefore this cannot be used to bypass the complete-artifact rule for
    # any newly-created migration.
    [[ -f "$source_path" ]] || return 1
    [[ -f "$designer_path" ]] || return 1
    git cat-file -e "${prior_ref}:${source_path}" 2>/dev/null || return 1

    if git cat-file -e "${prior_ref}:${designer_path}" 2>/dev/null; then
        return 1
    fi

    grep -Fq 'BuildTargetModel(ModelBuilder modelBuilder)' "$designer_path"
}

add_integrity_error() {
    INTEGRITY_ERRORS+=("$1")
}

migration_source_paths() {
    find "$MIGRATION_DIR" -type f -name '*.cs' \
        ! -name '*.Designer.cs' \
        ! -name 'MasterAppDbContextModelSnapshot.cs' \
        -print | sort
}

check_changed_artifact_set() {
    local source_changed=0
    local designer_changed=0
    local snapshot_changed=0
    local restored_designer=0
    local path file counterpart

    while IFS= read -r path; do
        [[ "$path" == "$MIGRATION_DIR/"* ]] || continue
        file="${path##*/}"
        case "$file" in
            MasterAppDbContextModelSnapshot.cs)
                snapshot_changed=1
                ;;
            *.Designer.cs)
                counterpart="${path%.Designer.cs}.cs"

                if path_changed "$counterpart"; then
                    designer_changed=1
                elif is_historical_designer_restoration "$path" "$counterpart"; then
                    restored_designer=1
                    printf '[MIGRATION] Restored missing historical designer: %s\n' "$file"
                else
                    add_integrity_error "Changed designer $file has no matching changed migration source and is not a verified historical restoration."
                fi
                ;;
            *.cs)
                source_changed=1
                if is_legacy_manual_migration "$path"; then
                    add_integrity_error "Legacy manual migration $file was changed; create a new generated migration instead."
                else
                    counterpart="${path%.cs}.Designer.cs"
                    if ! path_changed "$counterpart"; then
                        add_integrity_error "Changed migration $file is missing a matching changed designer."
                    fi
                fi
                ;;
        esac
    done <"$CHANGED_PATHS_FILE"

    # Newly created/changed migrations remain strict: source + designer +
    # snapshot must move together. A verified restoration of a historically
    # missing designer does not change the database model or migration source,
    # so it must not require falsifying changes to the canonical snapshot.
    if (( source_changed || designer_changed )) && (( ! snapshot_changed )); then
        add_integrity_error "Migration artifacts changed without MasterAppDbContextModelSnapshot.cs."
    fi

    if (( snapshot_changed )) && (( ! source_changed || ! designer_changed )); then
        add_integrity_error "MasterAppDbContextModelSnapshot.cs changed without a complete migration source/designer pair."
    fi

    if (( restored_designer )); then
        printf '[MIGRATION] Historical designer restoration verified against the prior committed state.\n'
    fi
}

legacy_baseline_was_already_committed() {
    local prior_ref=""
    if [[ -n "${DB_BASE_REF:-}" ]] && git cat-file -e "${DB_BASE_REF}^{commit}" 2>/dev/null; then
        prior_ref="$DB_BASE_REF"
    elif git rev-parse --verify HEAD >/dev/null 2>&1; then
        prior_ref="HEAD"
    fi

    [[ -n "$prior_ref" ]] && git cat-file -e "${prior_ref}:scripts/db-legacy-manual-migrations.txt" 2>/dev/null
}

check_migration_integrity() {
    local path file base designer
    INTEGRITY_ERRORS=()
    refresh_changed_paths
    printf '\n[MIGRATION] Checking migration artifact integrity\n'

    if [[ ! -f "$SNAPSHOT_FILE" ]]; then
        add_integrity_error "Missing $SNAPSHOT_FILE."
    fi

    if [[ ! -f "$LEGACY_MIGRATIONS" ]]; then
        add_integrity_error "Missing legacy migration baseline: scripts/db-legacy-manual-migrations.txt."
    fi

    while IFS= read -r path; do
        file="${path##*/}"
        base="${path%.cs}"
        designer="${base}.Designer.cs"

        if is_temporary_migration_name "$file"; then
            add_integrity_error "Temporary migration name is not allowed: $file."
        fi

        if ! is_legacy_manual_migration "$path" && [[ ! -f "$designer" ]]; then
            add_integrity_error "Migration source $file is missing $(basename "$designer")."
        fi
    done < <(migration_source_paths)

    while IFS= read -r path; do
        base="${path%.Designer.cs}"
        if [[ ! -f "${base}.cs" ]]; then
            add_integrity_error "Designer $(basename "$path") has no matching migration source."
        fi
    done < <(find "$MIGRATION_DIR" -type f -name '*.Designer.cs' -print | sort)

    while IFS= read -r file; do
        [[ -z "$file" || "$file" == \#* ]] && continue
        if [[ ! -f "$MIGRATION_DIR/$file" ]]; then
            add_integrity_error "Legacy migration baseline contains a missing file: $file."
        fi
    done <"$LEGACY_MIGRATIONS"

    if grep -R -nE '^(<<<<<<<|=======|>>>>>>>)' "$MIGRATION_DIR" --include='*.cs' >/dev/null 2>&1; then
        add_integrity_error "Unresolved merge conflict marker found in $MIGRATION_DIR."
    fi

    if git diff --name-only --diff-filter=U | grep -q .; then
        add_integrity_error "Git has unresolved merge conflicts."
    fi

    if path_changed "scripts/db-legacy-manual-migrations.txt" && legacy_baseline_was_already_committed; then
        add_integrity_error "The legacy migration baseline is immutable; new migrations require generated designers."
    fi

    check_changed_artifact_set

    if (( ${#INTEGRITY_ERRORS[@]} > 0 )); then
        local error
        for error in "${INTEGRITY_ERRORS[@]}"; do
            printf '[MIGRATION] INVALID: %s\n' "$error" >&2
        done
        return 1
    fi

    printf '[MIGRATION] Artifact set is valid.\n'
    return 0
}

extract_up_method() {
    local migration_file="$1"
    awk '
        /protected[[:space:]]+override[[:space:]]+void[[:space:]]+Up[[:space:]]*\(/ { in_up = 1 }
        /protected[[:space:]]+override[[:space:]]+void[[:space:]]+Down[[:space:]]*\(/ { in_up = 0 }
        in_up { print }
    ' "$migration_file"
}

add_manual_finding() {
    MANUAL_REVIEW_FINDINGS+=("$1")
}

scan_migration_for_manual_review() {
    local migration_file="$1"
    local up_file="$WORK_DIR/$(basename "$migration_file").up"
    local line text
    extract_up_method "$migration_file" >"$up_file"

    while IFS=: read -r line text; do
        add_manual_finding "$(basename "$migration_file"):$line: $text"
    done < <(grep -nE 'migrationBuilder\.(DropTable|DropColumn|AlterColumn|RenameTable|RenameColumn|RenameIndex|DropIndex|DropForeignKey|Sql|DeleteData|UpdateData|InsertData)\(' "$up_file" || true)

    while IFS='|' read -r line reason; do
        [[ -z "$line" ]] && continue
        add_manual_finding "$(basename "$migration_file"):$line: $reason"
    done < <(awk '
        /migrationBuilder\.AddColumn/ { in_column = 1; start = NR; block = $0 "\n"; next }
        in_column { block = block $0 "\n" }
        in_column && /\);/ {
            if (block ~ /nullable:[[:space:]]*false/ && block !~ /defaultValue:[[:space:]]*/) {
                print start "|AddColumn is non-nullable without an explicit safe default"
            }
            in_column = 0; block = ""
        }
    ' "$up_file")

    while IFS='|' read -r line reason; do
        [[ -z "$line" ]] && continue
        add_manual_finding "$(basename "$migration_file"):$line: $reason"
    done < <(awk '
        /migrationBuilder\.AddForeignKey/ { in_foreign_key = 1; start = NR; block = $0 "\n"; next }
        in_foreign_key { block = block $0 "\n" }
        in_foreign_key && /\);/ {
            if (block ~ /ReferentialAction\.Cascade/) {
                print start "|Added foreign key cascades deletes and requires review"
            }
            in_foreign_key = 0; block = ""
        }
    ' "$up_file")
}

scan_for_manual_review() {
    MANUAL_REVIEW_FINDINGS=()
    local migration
    for migration in "$@"; do
        scan_migration_for_manual_review "$migration"
    done

    if (( ${#MANUAL_REVIEW_FINDINGS[@]} == 0 )); then
        return 0
    fi

    printf '\n[MIGRATION] MANUAL REVIEW REQUIRED\n' >&2
    local finding
    for finding in "${MANUAL_REVIEW_FINDINGS[@]}"; do
        printf '  %s\n' "$finding" >&2
    done
    return 1
}

print_migration_summary() {
    local migration_file="$1"
    printf '\n[MIGRATION] Summary for %s\n' "${migration_file#$ROOT_DIR/}"
    extract_up_method "$migration_file" \
        | sed -n '/migrationBuilder\./,/^[[:space:]]*);/p' \
        | sed 's/^[[:space:]]*//' || true
}

load_pending_migration_files() {
    local output="$WORK_DIR/pending-migrations.log"
    PENDING_MIGRATION_FILES=()

    if ! run_ef migrations list \
        --project "$EF_PROJECT" \
        --startup-project "$STARTUP_PROJECT" \
        --context "$DB_CONTEXT" \
        --no-build >"$output" 2>&1; then
        cat "$output" >&2
        artifact_failure "EF could not inspect pending local migrations."
    fi

    local migration_id migration_file
    while IFS= read -r migration_id; do
        [[ -z "$migration_id" ]] && continue
        migration_file="$(find "$MIGRATION_DIR" -type f -name "${migration_id}.cs" -print -quit)"
        if [[ -z "$migration_file" ]]; then
            artifact_failure "Pending migration $migration_id has no source file in $MIGRATION_DIR."
        fi
        PENDING_MIGRATION_FILES+=("$migration_file")
    done < <(tr -d '\r' <"$output" | sed -n 's/ (Pending)$//p')
}

apply_local_migrations() {
    printf '\n[LOCAL DATABASE] Applying validated migrations\n'
    if ! run_ef database update \
        --project "$EF_PROJECT" \
        --startup-project "$STARTUP_PROJECT" \
        --context "$DB_CONTEXT" \
        --no-build; then
        printf '[RECOVERY] Inspect the local database and run ./scripts/db.sh apply again after resolving the error.\n' >&2
        exit 40
    fi
}

require_valid_migration_name() {
    local name="${1:-}"
    if [[ -z "$name" || ! "$name" =~ ^[A-Z][A-Za-z0-9]{2,80}$ ]]; then
        printf '[ERROR] MigrationName must be descriptive PascalCase (for example AddPrivateProfiles).\n' >&2
        printf '[RECOVERY] Run ./scripts/db.sh sync AddDescriptiveFeatureName\n' >&2
        exit 64
    fi

    local lower
    lower="$(printf '%s' "$name" | tr '[:upper:]' '[:lower:]')"
    case "$lower" in
        fix|update|migration|changes|test|temp|temporary|wip|scratch)
            printf '[ERROR] "%s" is too generic for a permanent migration.\n' "$name" >&2
            printf '[RECOVERY] Run ./scripts/db.sh sync AddDescriptiveFeatureName\n' >&2
            exit 64
            ;;
    esac
}

ensure_no_uncommitted_migrations() {
    if git status --porcelain -- "$MIGRATION_DIR" | grep -q .; then
        printf '[ERROR] A migration artifact is already uncommitted.\n' >&2
        printf '[RECOVERY] Validate, commit, or safely remove the existing migration before running sync.\n' >&2
        exit 20
    fi
}

print_commit_migration_files() {
    refresh_changed_paths
    printf '\n[MIGRATION] Files that belong in this commit:\n'
    local found=0 path
    while IFS= read -r path; do
        case "$path" in
            "$MIGRATION_DIR"/*)
                [[ "$path" == *.cs ]] || continue
                printf '  %s\n' "$path"
                found=1
                ;;
        esac
    done <"$CHANGED_PATHS_FILE"
    if (( ! found )); then
        printf '  No migration artifacts changed.\n'
    fi
}

command_check() {
    build_backend
    test_backend
    ensure_ef_tool
    check_model

    if ! check_migration_integrity; then
        result "MIGRATION ARTIFACTS INVALID"
        exit 20
    fi

    printf '\n[MIGRATION] Checking repository whitespace\n'
    if ! git diff --check; then
        artifact_failure "Git whitespace validation failed."
    fi

    if [[ "$MODEL_STATUS" == "required" ]]; then
        result "MIGRATION REQUIRED"
        exit 10
    fi

    result "DATABASE MODEL CLEAN"
}

command_sync() {
    local migration_name="${1:-}"
    local sources_before="$WORK_DIR/migration-sources-before.txt"
    local sources_after="$WORK_DIR/migration-sources-after.txt"
    local generated_sources="$WORK_DIR/generated-migration-sources.txt"

    build_backend
    test_backend
    ensure_ef_tool
    check_model

    if [[ "$MODEL_STATUS" == "clean" ]]; then
        printf '\n[MIGRATION] No migration required\n'
        result "DATABASE MODEL CLEAN"
        return 0
    fi

    require_valid_migration_name "$migration_name"
    ensure_no_uncommitted_migrations

    migration_source_paths >"$sources_before"

    printf '\n[MIGRATION] Generating %s\n' "$migration_name"
    if ! run_ef migrations add "$migration_name" \
        --project "$EF_PROJECT" \
        --startup-project "$STARTUP_PROJECT" \
        --context "$DB_CONTEXT"; then
        artifact_failure "EF could not generate migration $migration_name."
    fi

    build_backend
    test_backend
    check_model
    if [[ "$MODEL_STATUS" != "clean" ]]; then
        artifact_failure "The generated migration did not resolve the pending model changes."
    fi

    if ! check_migration_integrity; then
        result "MIGRATION ARTIFACTS INVALID"
        exit 20
    fi

    migration_source_paths >"$sources_after"
    comm -13 "$sources_before" "$sources_after" >"$generated_sources"

    local generated=()
    local path designer
    while IFS= read -r path; do
        [[ -n "$path" ]] && generated+=("$path")
    done <"$generated_sources"

    if (( ${#generated[@]} != 1 )); then
        artifact_failure "Expected exactly one migration source created by this sync invocation, found ${#generated[@]}."
    fi

    designer="${generated[0]%.cs}.Designer.cs"
    if [[ ! -f "$designer" ]]; then
        artifact_failure "The generated migration is missing its matching designer: $(basename "$designer")."
    fi

    refresh_changed_paths
    if ! path_changed "$SNAPSHOT_FILE"; then
        artifact_failure "The generated migration did not update $(basename "$SNAPSHOT_FILE")."
    fi

    if ! scan_for_manual_review "${generated[@]}"; then
        printf '[MIGRATION] File: %s\n' "${generated[0]}" >&2
        printf '[RECOVERY] If unapplied, undo it with: dotnet tool run dotnet-ef migrations remove --project Infrastructure/Infrastructure.csproj --startup-project AgentPortal/AgentPortal.csproj --context MasterAppDbContext\n' >&2
        exit 21
    fi

    print_migration_summary "${generated[0]}"

    load_pending_migration_files
    if ! scan_for_manual_review "${PENDING_MIGRATION_FILES[@]}"; then
        printf '[MIGRATION] File: %s\n' "${generated[0]}" >&2
        printf '[RECOVERY] If unapplied, undo it with: dotnet tool run dotnet-ef migrations remove --project Infrastructure/Infrastructure.csproj --startup-project AgentPortal/AgentPortal.csproj --context MasterAppDbContext\n' >&2
        exit 21
    fi

    apply_local_migrations

    load_pending_migration_files
    if (( ${#PENDING_MIGRATION_FILES[@]} > 0 )); then
        printf '[ERROR] Local migrations are still pending after apply.\n' >&2
        printf '[RECOVERY] Run ./scripts/db.sh apply after resolving the local database error.\n' >&2
        exit 40
    fi

    print_commit_migration_files
    git status --short
    result "LOCAL DATABASE SYNCHRONIZED"
}

command_apply() {
    build_backend
    test_backend
    ensure_ef_tool
    check_model
    if [[ "$MODEL_STATUS" != "clean" ]]; then
        result "MIGRATION REQUIRED"
        exit 10
    fi

    if ! check_migration_integrity; then
        result "MIGRATION ARTIFACTS INVALID"
        exit 20
    fi

    load_pending_migration_files
    if (( ${#PENDING_MIGRATION_FILES[@]} == 0 )); then
        printf '\n[LOCAL DATABASE] No local migrations are pending.\n'
        result "DATABASE MODEL CLEAN"
        return 0
    fi

    if ! scan_for_manual_review "${PENDING_MIGRATION_FILES[@]}"; then
        printf '[RECOVERY] Review the listed operations and apply the migration manually to a disposable local database if appropriate.\n' >&2
        exit 21
    fi

    apply_local_migrations
    load_pending_migration_files
    if (( ${#PENDING_MIGRATION_FILES[@]} > 0 )); then
        printf '[ERROR] Local migrations remain pending.\n' >&2
        printf '[RECOVERY] Resolve the database error, then run ./scripts/db.sh apply.\n' >&2
        exit 40
    fi

    result "LOCAL DATABASE SYNCHRONIZED"
}

command_validate() {
    build_backend
    test_backend
    ensure_ef_tool
    check_model
    if [[ "$MODEL_STATUS" != "clean" ]]; then
        result "MIGRATION REQUIRED"
        exit 10
    fi

    if ! check_migration_integrity; then
        result "MIGRATION ARTIFACTS INVALID"
        exit 20
    fi

    printf '\n[MIGRATION] Checking repository whitespace\n'
    if ! git diff --check; then
        artifact_failure "Git whitespace validation failed."
    fi

    print_commit_migration_files
    result "DATABASE MODEL CLEAN"
}

command_bundle() {
    command_validate
    printf '\n[BUNDLE] Creating %s\n' "${BUNDLE_PATH#$ROOT_DIR/}"
    if [[ -e "$BUNDLE_PATH" ]]; then
        artifact_failure "Refusing to overwrite an existing migration bundle. Archive or remove it explicitly, then run ./scripts/db.sh bundle."
    fi
    mkdir -p "$(dirname "$BUNDLE_PATH")"
    if ! run_ef migrations bundle \
        --project "$EF_PROJECT" \
        --startup-project "$STARTUP_PROJECT" \
        --context "$DB_CONTEXT" \
        --output "$BUNDLE_PATH"; then
        artifact_failure "EF could not create the migration bundle."
    fi

    printf '\n[DEPLOYMENT] Run artifacts/migrations/masterapp-migrate exactly once as a controlled deployment step before the application rollout.\n'
    printf '[DEPLOYMENT] The bundle was created only; this command never executes it.\n'
    result "MIGRATION BUNDLE READY"
}

case "${1:-}" in
    check)
        [[ $# -eq 1 ]] || { usage; exit 64; }
        command_check
        ;;
    sync)
        [[ $# -le 2 ]] || { usage; exit 64; }
        command_sync "${2:-}"
        ;;
    apply)
        [[ $# -eq 1 ]] || { usage; exit 64; }
        command_apply
        ;;
    validate)
        [[ $# -eq 1 ]] || { usage; exit 64; }
        command_validate
        ;;
    bundle)
        [[ $# -eq 1 ]] || { usage; exit 64; }
        command_bundle
        ;;
    -h|--help|help|"")
        usage
        ;;
    *)
        usage >&2
        exit 64
        ;;
esac
