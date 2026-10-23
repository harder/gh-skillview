#define _GNU_SOURCE
#include <fcntl.h>
#include <stddef.h>
#include <stdio.h>
#include <sys/stat.h>

/* Independent native-header evidence for managed Linux ABI constants. */
#define FLAG(name) printf(#name "=0x%llx\n", (unsigned long long)(name))

int main(void)
{
    FLAG(O_DIRECTORY); FLAG(O_NOFOLLOW); FLAG(O_DIRECT); FLAG(O_CLOEXEC);
    FLAG(O_PATH); FLAG(O_TMPFILE); FLAG(O_NONBLOCK); FLAG(O_CREAT); FLAG(O_EXCL);
    FLAG(AT_FDCWD); FLAG(AT_SYMLINK_NOFOLLOW); FLAG(AT_REMOVEDIR);
    printf("sizeof(stat)=%zu st_mode=%zu st_ino=%zu st_ctim=%zu sec=%zu nsec=%zu\n",
           sizeof(struct stat), offsetof(struct stat, st_mode), offsetof(struct stat, st_ino),
           offsetof(struct stat, st_ctim), offsetof(struct stat, st_ctim.tv_sec),
           offsetof(struct stat, st_ctim.tv_nsec));
    return 0;
}
