#define _GNU_SOURCE
#include <errno.h>
#include <dirent.h>
#include <fcntl.h>
#include <limits.h>
#include <linux/audit.h>
#include <linux/capability.h>
#include <linux/filter.h>
#include <linux/sched.h>
#include <linux/seccomp.h>
#include <poll.h>
#include <signal.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/prctl.h>
#include <sys/resource.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <sys/sysmacros.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

/* Intentionally one audited ABI. Cross-ABI/x32 calls are killed, not emulated. */
#if !defined(__linux__) || !defined(__x86_64__) || defined(__ILP32__)
#error This launcher supports Linux x86_64 LP64 only
#endif

#define GIB (1024ULL * 1024ULL * 1024ULL)
#define DENY(name) BPF_JUMP(BPF_JMP|BPF_JEQ|BPF_K, __NR_##name, 0, 1), BPF_STMT(BPF_RET|BPF_K, SECCOMP_RET_ERRNO | EPERM)
#define LOAD_NR BPF_STMT(BPF_LD|BPF_W|BPF_ABS, offsetof(struct seccomp_data, nr))
static volatile sig_atomic_t cancelled;
static void cancel(int sig) { (void)sig; cancelled = 1; }
static void fail(const char *code) { fprintf(stderr, "legend_launcher:%s\n", code); _exit(125); }
static void require(int ok, const char *code) { if (!ok) fail(code); }
static void check_capabilities(void) {
  struct __user_cap_header_struct header = {_LINUX_CAPABILITY_VERSION_3, 0};
  struct __user_cap_data_struct data[2] = {{0}};
  require(syscall(__NR_capget, &header, data) == 0, "capabilities_unavailable");
  for (int i = 0; i < 2; i++) require(!data[i].effective && !data[i].permitted && !data[i].inheritable, "capabilities_must_be_empty");
}
static void limit(int resource, rlim_t value) {
  struct rlimit current;
  require(getrlimit(resource, &current) == 0, "getrlimit_failed");
  struct rlimit bound = {value < current.rlim_max ? value : current.rlim_max,
                        value < current.rlim_max ? value : current.rlim_max};
  require(setrlimit(resource, &bound) == 0, "setrlimit_failed");
}
static int64_t millis(void) {
  struct timespec ts;
  require(clock_gettime(CLOCK_MONOTONIC, &ts) == 0, "clock_failed");
  return (int64_t)ts.tv_sec * 1000 + ts.tv_nsec / 1000000;
}
static void check_stdio(int parent_ipc) {
  for (int fd = 0; fd <= 2; fd++) {
    struct stat st;
    require(fstat(fd, &st) == 0, "stdio_missing");
    int local_socket = 0;
    if (parent_ipc && S_ISSOCK(st.st_mode)) {
      int domain = 0; socklen_t length = sizeof(domain);
      local_socket = getsockopt(fd, SOL_SOCKET, SO_DOMAIN, &domain, &length) == 0 && domain == AF_UNIX;
    }
    require(local_socket || S_ISREG(st.st_mode) || S_ISFIFO(st.st_mode)
      || (S_ISCHR(st.st_mode) && major(st.st_rdev) == 1 && minor(st.st_rdev) == 3), "stdio_not_local_ipc_file_or_null");
  }
}
static void clear_descriptors(void) {
  if (syscall(__NR_close_range, 3U, UINT_MAX, 0) == 0) return;
  /* Single-threaded trusted launcher only. Enumerate the complete kernel FD
     table; never guess a range from a lowered limit or omit large-number FDs. */
  DIR *directory = opendir("/proc/self/fd");
  require(directory != NULL, "descriptor_enumeration_failed");
  int own = dirfd(directory);
  require(own >= 3, "descriptor_directory_invalid");
  struct dirent *entry;
  errno = 0;
  while ((entry = readdir(directory)) != NULL) {
    if (entry->d_name[0] == '.') continue;
    char *end = NULL; long fd = strtol(entry->d_name, &end, 10);
    require(end != entry->d_name && !*end && fd >= 0 && fd <= INT_MAX, "descriptor_entry_invalid");
    if (fd >= 3 && fd != own) require(close((int)fd) == 0, "descriptor_close_failed");
    errno = 0;
  }
  require(errno == 0 && closedir(directory) == 0, "descriptor_enumeration_incomplete");
}
static void environment(void) {
  require(clearenv() == 0, "clearenv_failed");
  const char *pairs[][2] = {
    {"PATH", "/opt/node/bin:/usr/share/dotnet:/usr/local/bin:/usr/bin:/bin"},
    {"HOME", "/workspace/home"}, {"TMPDIR", "/workspace/tmp"}, {"LANG", "C.UTF-8"},
    {"DOTNET_ROOT", "/usr/share/dotnet"}, {"DOTNET_CLI_HOME", "/workspace/home"},
    {"DOTNET_CLI_TELEMETRY_OPTOUT", "1"}, {"DOTNET_NOLOGO", "1"},
    {"DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "1"}, {"DOTNET_EnableDiagnostics", "0"},
    {"DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE", "true"}, {"DOTNET_PROCESSOR_COUNT", "2"},
    {"DOTNET_GCHeapHardLimit", "0x80000000"}, {"MSBUILDDISABLENODEREUSE", "1"},
    {"DOTNET_CLI_USE_MSBUILD_SERVER", "0"}, {"DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER", "1"},
    {"NUGET_PACKAGES", "/opt/nuget/packages"}, {"NODE_OPTIONS", "--max-old-space-size=2048"},
  };
  for (size_t i = 0; i < sizeof(pairs) / sizeof(pairs[0]); i++)
    require(setenv(pairs[i][0], pairs[i][1], 1) == 0, "environment_failed");
}
static void install_filter(pid_t child) {
  /* All socket families are denied, including AF_UNIX/NETLINK/PACKET. This
     prevents DNS, local proxies, SCM_RIGHTS, and raw-packet alternatives. */
  struct sock_filter filter[] = {
    BPF_STMT(BPF_LD|BPF_W|BPF_ABS, offsetof(struct seccomp_data, arch)),
    BPF_JUMP(BPF_JMP|BPF_JEQ|BPF_K, AUDIT_ARCH_X86_64, 1, 0),
    BPF_STMT(BPF_RET|BPF_K, SECCOMP_RET_KILL_PROCESS),
    LOAD_NR,
    BPF_JUMP(BPF_JMP|BPF_JSET|BPF_K, 0x40000000U, 0, 1),
    BPF_STMT(BPF_RET|BPF_K, SECCOMP_RET_KILL_PROCESS),
    DENY(socket), DENY(socketpair), DENY(connect), DENY(bind), DENY(listen),
    DENY(accept), DENY(accept4), DENY(sendto), DENY(sendmsg), DENY(sendmmsg),
    DENY(recvfrom), DENY(recvmsg), DENY(recvmmsg),
    DENY(io_uring_setup), DENY(io_uring_enter), DENY(io_uring_register),
    DENY(ptrace), DENY(process_vm_readv), DENY(process_vm_writev),
    DENY(pidfd_getfd), DENY(pidfd_send_signal), DENY(bpf), DENY(perf_event_open),
    DENY(userfaultfd), DENY(open_by_handle_at), DENY(mount), DENY(umount2),
    DENY(pivot_root), DENY(chroot), DENY(unshare), DENY(setns),
    DENY(kexec_load), DENY(reboot), DENY(init_module), DENY(finit_module),
    DENY(delete_module), DENY(keyctl), DENY(add_key), DENY(request_key),
    DENY(setuid), DENY(setgid), DENY(setreuid), DENY(setregid),
    DENY(setresuid), DENY(setresgid), DENY(setfsuid), DENY(setfsgid), DENY(setgroups),
    DENY(setsid), DENY(setpgid), DENY(kill), DENY(tkill),
    DENY(rt_sigqueueinfo), DENY(rt_tgsigqueueinfo),
    DENY(ioctl),
    /* Async descriptor notification can otherwise signal another same-UID task. */
    BPF_JUMP(BPF_JMP|BPF_JEQ|BPF_K, __NR_fcntl, 0, 8),
    BPF_STMT(BPF_LD|BPF_W|BPF_ABS, offsetof(struct seccomp_data, args[1])),
    BPF_JUMP(BPF_JMP|BPF_JEQ|BPF_K, F_SETOWN, 5, 0),
    BPF_JUMP(BPF_JMP|BPF_JEQ|BPF_K, F_SETOWN_EX, 4, 0),
    BPF_JUMP(BPF_JMP|BPF_JEQ|BPF_K, F_SETSIG, 3, 0),
    BPF_JUMP(BPF_JMP|BPF_JEQ|BPF_K, F_SETLEASE, 2, 0),
    BPF_JUMP(BPF_JMP|BPF_JEQ|BPF_K, F_NOTIFY, 1, 0),
    BPF_STMT(BPF_RET|BPF_K, SECCOMP_RET_ALLOW),
    BPF_STMT(BPF_RET|BPF_K, SECCOMP_RET_ERRNO | EPERM),
    BPF_JUMP(BPF_JMP|BPF_JEQ|BPF_K, __NR_prlimit64, 0, 4),
    BPF_STMT(BPF_LD|BPF_W|BPF_ABS, offsetof(struct seccomp_data, args[0])),
    BPF_JUMP(BPF_JMP|BPF_JEQ|BPF_K, 0, 1, 0),
    BPF_STMT(BPF_RET|BPF_K, SECCOMP_RET_ERRNO | EPERM),
    BPF_STMT(BPF_RET|BPF_K, SECCOMP_RET_ALLOW),
    /* Keep pthread signals within the initial child tgid; descendants cannot
       signal the unfiltered supervisor. Compatibility must be qualified. */
    BPF_JUMP(BPF_JMP|BPF_JEQ|BPF_K, __NR_tgkill, 0, 4),
    BPF_STMT(BPF_LD|BPF_W|BPF_ABS, offsetof(struct seccomp_data, args[0])),
    BPF_JUMP(BPF_JMP|BPF_JEQ|BPF_K, (uint32_t)child, 1, 0),
    BPF_STMT(BPF_RET|BPF_K, SECCOMP_RET_ERRNO | EPERM),
    BPF_STMT(BPF_RET|BPF_K, SECCOMP_RET_ALLOW),
    /* clone3 takes an indirect struct that classic BPF cannot inspect. glibc
       can fall back to clone on ENOSYS, whose namespace flags we can inspect. */
    BPF_JUMP(BPF_JMP|BPF_JEQ|BPF_K, __NR_clone3, 0, 1),
    BPF_STMT(BPF_RET|BPF_K, SECCOMP_RET_ERRNO | ENOSYS),
    BPF_JUMP(BPF_JMP|BPF_JEQ|BPF_K, __NR_clone, 0, 4),
    BPF_STMT(BPF_LD|BPF_W|BPF_ABS, offsetof(struct seccomp_data, args[0])),
    BPF_JUMP(BPF_JMP|BPF_JSET|BPF_K, CLONE_NEWUSER|CLONE_NEWPID|CLONE_NEWNET|CLONE_NEWNS|CLONE_NEWIPC|CLONE_NEWUTS|CLONE_NEWCGROUP, 0, 1),
    BPF_STMT(BPF_RET|BPF_K, SECCOMP_RET_ERRNO | EPERM),
    BPF_STMT(BPF_RET|BPF_K, SECCOMP_RET_ALLOW),
    BPF_STMT(BPF_RET|BPF_K, SECCOMP_RET_ALLOW),
  };
  struct sock_fprog program = {(unsigned short)(sizeof(filter) / sizeof(filter[0])), filter};
  require(prctl(PR_SET_NO_NEW_PRIVS, 1, 0, 0, 0) == 0, "no_new_privs_failed");
  require(prctl(PR_SET_SECCOMP, SECCOMP_MODE_FILTER, &program) == 0, "seccomp_unavailable");
}

int main(int argc, char **argv) {
  if (argc == 2 && !strcmp(argv[1], "--idle")) {
    require(getuid() == 1654 && geteuid() == 1654, "nonroot_identity_required");
    check_capabilities(); check_stdio(1); clear_descriptors(); environment();
    require(prctl(PR_SET_DUMPABLE, 0) == 0, "dump_protection_failed");
    /* Startup-to-stop failsafe; controller must still destroy by its deadline. */
    struct timespec idle = {630, 0};
    while (nanosleep(&idle, &idle) && errno == EINTR) {}
    return 0;
  }
  if (argc < 5 || strcmp(argv[1], "--wall-seconds") || strcmp(argv[3], "--") || argv[4][0] != '/') fail("invalid_arguments");
  char *end = NULL;
  errno = 0;
  long wall = strtol(argv[2], &end, 10);
  require(!errno && end != argv[2] && !*end && wall >= 1 && wall <= 600, "invalid_deadline");
  require(getuid() == 1654 && geteuid() == 1654 && getgid() == 1654 && getegid() == 1654, "nonroot_identity_required");
  gid_t groups[32]; int count = getgroups(32, groups);
  require(count >= 0, "groups_unavailable");
  for (int i = 0; i < count; i++) require(groups[i] == 1654, "unexpected_supplementary_group");
  check_capabilities(); check_stdio(1); clear_descriptors(); environment(); umask(077);
  require(prctl(PR_SET_DUMPABLE, 0) == 0, "dump_protection_failed");
  require(prctl(PR_SET_CHILD_SUBREAPER, 1) == 0, "subreaper_failed");
  require(chdir("/workspace") == 0, "workspace_missing");
  struct sigaction action = {.sa_handler = cancel};
  sigemptyset(&action.sa_mask);
  require(sigaction(SIGTERM, &action, NULL) == 0 && sigaction(SIGINT, &action, NULL) == 0, "signal_setup_failed");
  require(signal(SIGPIPE, SIG_IGN) != SIG_ERR, "pipe_signal_failed");
  const int64_t deadline = millis() + wall * 1000;
  int output[2], errors[2];
  require(pipe2(output, O_CLOEXEC) == 0 && pipe2(errors, O_CLOEXEC) == 0, "output_pipe_failed");
  const pid_t supervisor = getpid();
  pid_t child = fork();
  require(child >= 0, "fork_failed");
  if (!child) {
    require(prctl(PR_SET_PDEATHSIG, SIGKILL) == 0 && getppid() == supervisor, "supervisor_lost");
    require(setpgid(0, 0) == 0, "process_group_failed");
    int nullfd = open("/dev/null", O_RDONLY|O_CLOEXEC);
    require(nullfd >= 0 && dup2(nullfd, 0) == 0 && dup2(output[1], 1) == 1 && dup2(errors[1], 2) == 2, "stdio_redirection_failed");
    clear_descriptors();
    check_stdio(0);
    require(signal(SIGTERM, SIG_DFL) != SIG_ERR && signal(SIGINT, SIG_DFL) != SIG_ERR && signal(SIGPIPE, SIG_DFL) != SIG_ERR, "child_signals_failed");
    limit(RLIMIT_CORE, 0); limit(RLIMIT_CPU, (rlim_t)wall * 2);
    limit(RLIMIT_AS, 32 * GIB); limit(RLIMIT_NOFILE, 256);
    limit(RLIMIT_NPROC, 256); limit(RLIMIT_FSIZE, GIB); limit(RLIMIT_MEMLOCK, 0);
    install_filter(getpid());
    execv(argv[4], &argv[4]);
    fail("exec_failed");
  }
  /* Parent installs the group too to close the early-timeout race. */
  if (setpgid(child, child) && errno != EACCES && errno != ESRCH) {
    kill(child, SIGKILL); fail("parent_group_failed");
  }
  close(output[1]); close(errors[1]);
  int status = 0; int timed_out = 0; int output_failed = 0; int reaped = 0;
  size_t emitted = 0;
  struct pollfd pipes[2] = {{output[0], POLLIN, 0}, {errors[0], POLLIN, 0}};
  for (int i = 0; i < 2; i++) {
    int flags = fcntl(pipes[i].fd, F_GETFL);
    if (flags < 0 || fcntl(pipes[i].fd, F_SETFL, flags|O_NONBLOCK)) output_failed = 1;
    flags = fcntl(i+1, F_GETFL);
    if (flags < 0 || fcntl(i+1, F_SETFL, flags|O_NONBLOCK)) output_failed = 1;
  }
  for (;;) {
    pid_t done = reaped ? 0 : waitpid(child, &status, WNOHANG);
    if (done == child) { reaped = 1; kill(-child, SIGKILL); }
    if (done < 0 && errno != EINTR) { kill(-child, SIGKILL); fail("wait_failed"); }
    if (output_failed || (reaped && pipes[0].fd < 0 && pipes[1].fd < 0)) break;
    if (cancelled || millis() >= deadline) { timed_out = 1; break; }
    if (poll(pipes, 2, 10) < 0 && errno != EINTR) { output_failed = 1; break; }
    for (int i = 0; i < 2 && !output_failed; i++) {
      if (pipes[i].fd < 0 || !pipes[i].revents) continue;
      char buffer[4096]; ssize_t size;
      while ((size = read(pipes[i].fd, buffer, sizeof(buffer))) > 0) {
        emitted += (size_t)size;
        if (emitted > 1024 * 1024 || write(i+1, buffer, (size_t)size) != size) { output_failed = 1; break; }
      }
      if (size == 0) { close(pipes[i].fd); pipes[i].fd = -1; }
      else if (size < 0 && errno != EAGAIN && errno != EINTR) output_failed = 1;
    }
  }
  /* Descendants cannot escape this group: setsid/setpgid/new namespaces denied. */
  int kill_ok = kill(-child, SIGKILL) == 0 || errno == ESRCH;
  for (int i = 0; i < 2; i++) if (pipes[i].fd >= 0) close(pipes[i].fd);
  const int64_t reap_deadline = millis() + 1000;
  int cleanup_verified = 0;
  while (millis() < reap_deadline) {
    int descendant_status = 0;
    pid_t done = waitpid(-1, &descendant_status, WNOHANG);
    if (done == child) { status = descendant_status; reaped = 1; }
#ifdef LEGEND_TEST_UNVERIFIED_CLEANUP
    /* Compile-time fault injection exists only in the test image binary. */
    if (done < 0 && errno == ECHILD) { done = 0; errno = 0; }
#endif
    if (done < 0 && errno == ECHILD) { cleanup_verified = 1; break; }
    if (done < 0 && errno != EINTR) break;
    struct timespec pause = {0, 10000000}; nanosleep(&pause, NULL);
  }
  if (!kill_ok || !cleanup_verified || !reaped) { fprintf(stderr, "legend_launcher:cleanup_unverified\n"); return 127; }
  if (timed_out) { fprintf(stderr, "legend_launcher:deadline_or_cancel\n"); return 124; }
  if (output_failed) { fprintf(stderr, "legend_launcher:output_limit_or_transport\n"); return 126; }
  return WIFEXITED(status) ? WEXITSTATUS(status) : 128 + WTERMSIG(status);
}
