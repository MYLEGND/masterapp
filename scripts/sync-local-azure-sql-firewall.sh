#!/usr/bin/env bash
# Keeps one local-development Azure SQL firewall rule aligned with this Mac's
# current public IPv4 address. This runs locally under the signed-in Azure CLI
# identity; the portal never receives Azure management credentials.

set -Eeuo pipefail
IFS=$'\n\t'

readonly SUBSCRIPTION_ID="a543b54e-8a42-4a4b-bd4d-bc13573b1c48"
readonly RESOURCE_GROUP="MasterApp-Data-RG"
readonly SQL_SERVER="masterapp-sql-prod-1221"
readonly SQL_SERVER_FQDN="masterapp-sql-prod-1221.database.windows.net"
readonly FIREWALL_RULE="ZacLocalDev-AutoSync"

if [[ -x /opt/homebrew/bin/az ]]; then
    readonly AZ_BIN="/opt/homebrew/bin/az"
elif [[ -x /usr/local/bin/az ]]; then
    readonly AZ_BIN="/usr/local/bin/az"
else
    printf 'Azure CLI was not found. Install Azure CLI and sign in before running this synchronizer.\n' >&2
    exit 127
fi

fail() {
    printf '[azure-sql-firewall-sync] %s\n' "$1" >&2
    exit 1
}

is_ipv4() {
    local candidate="$1"
    local -a octets

    [[ "$candidate" =~ ^([0-9]{1,3}\.){3}[0-9]{1,3}$ ]] || return 1
    IFS='.' read -r -a octets <<< "$candidate"

    local octet
    for octet in "${octets[@]}"; do
        (( 10#$octet <= 255 )) || return 1
    done
}

is_public_ipv4() {
    local candidate="$1"
    local -a octets

    is_ipv4 "$candidate" || return 1
    IFS='.' read -r -a octets <<< "$candidate"

    local first=$((10#${octets[0]}))
    local second=$((10#${octets[1]}))

    # Azure SQL treats 0.0.0.0 specially as "all Azure services". Never let
    # a local synchronizer create that broad rule, nor any non-public range.
    (( first >= 1 && first <= 223 )) || return 1
    (( first != 10 && first != 127 )) || return 1
    ! (( first == 169 && second == 254 )) || return 1
    ! (( first == 172 && second >= 16 && second <= 31 )) || return 1
    ! (( first == 192 && second == 168 )) || return 1
    ! (( first == 100 && second >= 64 && second <= 127 )) || return 1
}

current_public_ipv4() {
    local ip
    ip="$(/usr/bin/curl --fail --silent --connect-timeout 5 --max-time 15 https://api.ipify.org 2>/dev/null || true)"
    if ! is_ipv4 "$ip"; then
        ip="$(/usr/bin/dig +short myip.opendns.com @resolver1.opendns.com 2>/dev/null | /usr/bin/awk 'NR == 1 { print; exit }')"
    fi
    ip="${ip//$'\r'/}"
    ip="${ip//$'\n'/}"
    is_public_ipv4 "$ip" || fail 'Unable to determine a valid public IPv4 address.'
    printf '%s' "$ip"
}

assert_azure_identity() {
    local active_subscription server_fqdn

    active_subscription="$("$AZ_BIN" account show --query id --output tsv --only-show-errors)" \
        || fail 'Azure CLI is not authenticated.'
    [[ "$active_subscription" == "$SUBSCRIPTION_ID" ]] \
        || fail "Azure CLI is using unexpected subscription $active_subscription."

    server_fqdn="$("$AZ_BIN" sql server show \
        --subscription "$SUBSCRIPTION_ID" \
        --resource-group "$RESOURCE_GROUP" \
        --name "$SQL_SERVER" \
        --query fullyQualifiedDomainName \
        --output tsv \
        --only-show-errors)" || fail 'Unable to resolve the expected Azure SQL server.'
    [[ "$server_fqdn" == "$SQL_SERVER_FQDN" ]] \
        || fail "Azure SQL server identity mismatch: $server_fqdn"
}

main() {
    [[ $# -eq 0 ]] || fail 'This synchronizer does not accept arguments.'

    local current_ip existing_ip
    current_ip="$(current_public_ipv4)"
    assert_azure_identity

    existing_ip="$("$AZ_BIN" sql server firewall-rule show \
        --subscription "$SUBSCRIPTION_ID" \
        --resource-group "$RESOURCE_GROUP" \
        --server "$SQL_SERVER" \
        --name "$FIREWALL_RULE" \
        --query startIpAddress \
        --output tsv \
        --only-show-errors 2>/dev/null || true)"

    if [[ "$existing_ip" == "$current_ip" ]]; then
        return 0
    fi

    "$AZ_BIN" sql server firewall-rule create \
        --subscription "$SUBSCRIPTION_ID" \
        --resource-group "$RESOURCE_GROUP" \
        --server "$SQL_SERVER" \
        --name "$FIREWALL_RULE" \
        --start-ip-address "$current_ip" \
        --end-ip-address "$current_ip" \
        --output none \
        --only-show-errors || fail 'Azure SQL firewall-rule synchronization failed.'

    printf '[azure-sql-firewall-sync] %s synchronized to %s\n' "$FIREWALL_RULE" "$current_ip"
}

main "$@"
