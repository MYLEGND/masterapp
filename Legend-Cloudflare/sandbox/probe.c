#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <linux/sched.h>
#include <linux/filter.h>
#include <linux/seccomp.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/prctl.h>
#include <sys/resource.h>
#include <sys/socket.h>
#include <sys/syscall.h>
#include <sys/wait.h>
#include <unistd.h>
static void check(int ok, const char *name) { if (!ok) { fprintf(stderr,"probe_failed:%s errno=%d\n",name,errno); exit(1); } }
#define BLOCKED(expr) do { errno=0; check((expr)==-1 && errno==EPERM, #expr); } while(0)
int main(int argc, char **argv) {
  if (argc > 1 && !strcmp(argv[1], "seccomp-support")) {
    struct sock_filter filter[] = {BPF_STMT(BPF_RET|BPF_K, SECCOMP_RET_ALLOW)};
    struct sock_fprog program = {1, filter};
    if (prctl(PR_SET_NO_NEW_PRIVS,1,0,0,0) || prctl(PR_SET_SECCOMP,SECCOMP_MODE_FILTER,&program)) {
      fprintf(stderr,"host_seccomp_unavailable errno=%d\n",errno); return 125;
    }
    puts("host_seccomp_available"); return 0;
  }
  if (argc > 1 && !strcmp(argv[1], "hang")) { puts("probe_hanging"); fflush(stdout); for (;;) pause(); }
  if (argc > 1 && !strcmp(argv[1], "x32")) { syscall(SYS_getpid | 0x40000000U); return 3; }
  if (argc > 1 && !strcmp(argv[1], "flood")) { char bytes[4096]; memset(bytes,'x',sizeof(bytes)); for (;;) { if (write(1,bytes,sizeof(bytes)) < 0) return 0; } }
  if (argc > 1 && !strcmp(argv[1], "orphan")) {
    pid_t child = fork(); check(child >= 0, "fork");
    if (!child) { for (;;) pause(); }
    printf("%d\n", child); fflush(stdout); return 0;
  }
  check(getuid()==1654 && geteuid()==1654, "nonroot");
  check(prctl(PR_GET_NO_NEW_PRIVS,0,0,0,0)==1, "no_new_privs");
  check(prctl(PR_GET_SECCOMP,0,0,0,0)==2, "seccomp");
  check(getenv("LEGEND_TEST_SECRET")==NULL && getenv("LD_PRELOAD")==NULL, "cleared_environment");
  check(getenv("HOME") && !strcmp(getenv("HOME"),"/workspace/home"), "fixed_home");
  errno=0; check(fcntl(9,F_GETFD)==-1 && errno==EBADF,"inherited_fd_closed");
  for(int family=0; family<4; family++) {
    int families[]={AF_INET,AF_INET6,AF_UNIX,AF_NETLINK};
    BLOCKED(socket(families[family],SOCK_STREAM,0));
    BLOCKED(socket(families[family],SOCK_DGRAM,0));
  }
  int pair[2]; BLOCKED(socketpair(AF_UNIX,SOCK_STREAM,0,pair));
  BLOCKED(syscall(SYS_connect,-1,NULL,0));
  BLOCKED(syscall(SYS_sendto,-1,NULL,0,0,NULL,0));
  BLOCKED(syscall(SYS_sendmsg,-1,NULL,0));
  BLOCKED(syscall(SYS_io_uring_setup,0,NULL));
  BLOCKED(syscall(SYS_pidfd_getfd,-1,1,0));
  BLOCKED(syscall(SYS_process_vm_writev,getppid(),NULL,0,NULL,0,0));
  BLOCKED(syscall(SYS_ptrace,0,0,0,0));
  BLOCKED(kill(getppid(),SIGTERM));
  BLOCKED(syscall(SYS_tgkill,getppid(),getppid(),SIGTERM));
  BLOCKED(setsid()); BLOCKED(setpgid(0,0));
  BLOCKED(syscall(SYS_clone,CLONE_NEWUSER|SIGCHLD,NULL,NULL,NULL,0));
  BLOCKED(syscall(SYS_prlimit64,getppid(),RLIMIT_CPU,NULL,NULL));
  BLOCKED(fcntl(1,F_SETOWN,getppid()));
  pid_t supervisor = argc > 2 ? (pid_t)atoi(argv[2]) : getppid();
  char mempath[80]; snprintf(mempath,sizeof(mempath),"/proc/%d/mem",supervisor);
  errno=0; check(open(mempath,O_RDWR)==-1 && (errno==EACCES || errno==EPERM),"supervisor_mem_denied");
  for (int fd=1; fd<=2; fd++) {
    snprintf(mempath,sizeof(mempath),"/proc/%d/fd/%d",supervisor,fd);
    errno=0; check(open(mempath,O_WRONLY)==-1 && (errno==EACCES || errno==EPERM),"supervisor_output_fd_denied");
  }
  struct rlimit lim; check(getrlimit(RLIMIT_NOFILE,&lim)==0 && lim.rlim_max<=256,"fd_limit");
  lim.rlim_cur=lim.rlim_max=512; BLOCKED(setrlimit(RLIMIT_NOFILE,&lim));
  check(getrlimit(RLIMIT_CORE,&lim)==0 && lim.rlim_max==0,"no_core");
  check(getrlimit(RLIMIT_NPROC,&lim)==0 && lim.rlim_max<=256,"process_limit");
  check(getrlimit(RLIMIT_AS,&lim)==0 && lim.rlim_max<=32ULL*1024*1024*1024,"address_limit");
  if (argc < 2 || strcmp(argv[1],"descendant")) {
    pid_t child=fork(); check(child>=0,"inheritance_fork");
    if (!child) { char pidtext[32]; snprintf(pidtext,sizeof(pidtext),"%d",supervisor); execl(argv[0],argv[0],"descendant",pidtext,NULL); _exit(127); }
    int status; check(waitpid(child,&status,0)==child && WIFEXITED(status) && WEXITSTATUS(status)==0,"inherited_filter_after_exec");
  }
  puts("probe_passed"); return 0;
}
