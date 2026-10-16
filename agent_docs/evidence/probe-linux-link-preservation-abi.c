#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <inttypes.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/sysmacros.h>
#include <sys/types.h>
#include <sys/vfs.h>
#include <unistd.h>

/* Native-header evidence only; this probe does not enable product admission.
 * Run with an owned private directory as argv[1], or use a private CWD.
 * f_type == 0xef53 is shared by ext2/ext3/ext4, not proof of ext4 alone. */
struct guarded_statfs {
    unsigned char before[64];
    struct statfs value;
    unsigned char after[64];
};

struct device_case {
    uint32_t major_number;
    uint32_t minor_number;
};

static const struct device_case device_cases[] = {
    {0, 0}, {1, 1}, {8, 48},
    {0xfff, 0xff}, {0x1000, 0x100},
    {0xfffff, 0xfffff},
    {0x12345678, 0x9abcdef0},
    {UINT32_MAX, 0}, {0, UINT32_MAX}, {UINT32_MAX, UINT32_MAX}
};

static int canary_matches(const unsigned char *bytes, size_t length,
                          unsigned char expected)
{
    for (size_t i = 0; i < length; ++i)
        if (bytes[i] != expected) return 0;
    return 1;
}

static const char *architecture(void)
{
#if defined(__x86_64__)
    return "x64";
#elif defined(__aarch64__)
    return "arm64";
#else
    return "unverified";
#endif
}

static const char *endianness(void)
{
    const uint32_t value = UINT32_C(0x01020304);
    const unsigned char *bytes = (const unsigned char *)&value;
    if (bytes[0] == 4 && bytes[1] == 3 && bytes[2] == 2 && bytes[3] == 1)
        return "little";
    if (bytes[0] == 1 && bytes[1] == 2 && bytes[2] == 3 && bytes[3] == 4)
        return "big";
    return "unverified";
}

static int failure(const char *stage, int error)
{
    printf("{\"schema\":1,\"ok\":false,\"stage\":\"%s\",\"errno\":%d}\n",
           stage, error);
    return 1;
}

int main(int argc, char **argv)
{
    if (argc > 2) return failure("arguments", EINVAL);
    const char *path = argc == 2 ? argv[1] : ".";
    int fd = open(path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
    if (fd < 0) return failure("open-held-directory", errno);

    struct guarded_statfs guarded;
    memset(&guarded, 0xa5, sizeof(guarded));
    memset(guarded.before, 0xc3, sizeof(guarded.before));
    memset(guarded.after, 0x3c, sizeof(guarded.after));
    int fs_result = fstatfs(fd, &guarded.value);
    int fs_error = fs_result == 0 ? 0 : errno;
    struct stat identity;
    int stat_result = fstat(fd, &identity);
    int stat_error = stat_result == 0 ? 0 : errno;
    /* close is deliberately not retried, even if interrupted. */
    int close_result = close(fd);
    int close_error = close_result == 0 ? 0 : errno;
    int before_ok = canary_matches(guarded.before, sizeof(guarded.before), 0xc3);
    int after_ok = canary_matches(guarded.after, sizeof(guarded.after), 0x3c);
    if (!before_ok || !after_ok) return failure("fstatfs-canaries", EOVERFLOW);
    if (fs_result != 0) return failure("fstatfs", fs_error);
    if (stat_result != 0) return failure("fstat", stat_error);
    if (close_result != 0) return failure("close", close_error);
    if (!S_ISDIR(identity.st_mode)) return failure("directory-type", ENOTDIR);

    int devices_ok = 1;
    for (size_t i = 0; i < sizeof(device_cases) / sizeof(device_cases[0]); ++i) {
        dev_t encoded = makedev(device_cases[i].major_number, device_cases[i].minor_number);
        if ((uintmax_t)major(encoded) != device_cases[i].major_number
            || (uintmax_t)minor(encoded) != device_cases[i].minor_number)
            devices_ok = 0;
    }

    printf("{\"schema\":1,\"ok\":%s,\"architecture\":\"%s\","
           "\"endianness\":\"%s\",\"pointerBytes\":%zu,\"longBytes\":%zu,"
           "\"devTBytes\":%zu,\"devTAlignment\":%zu,"
           "\"stat\":{\"size\":%zu,\"alignment\":%zu,"
           "\"devOffset\":%zu,\"devBytes\":%zu,\"inoOffset\":%zu,\"inoBytes\":%zu,"
           "\"modeOffset\":%zu,\"modeBytes\":%zu,\"nlinkOffset\":%zu,\"nlinkBytes\":%zu,"
           "\"uidOffset\":%zu,\"uidBytes\":%zu,\"gidOffset\":%zu,\"gidBytes\":%zu,"
           "\"ctimeSecondsOffset\":%zu,\"ctimeSecondsBytes\":%zu,"
           "\"ctimeNanosecondsOffset\":%zu,\"ctimeNanosecondsBytes\":%zu},"
           "\"statfs\":{\"size\":%zu,\"alignment\":%zu,"
           "\"typeOffset\":%zu,\"typeBytes\":%zu,"
           "\"fsidOffset\":%zu,\"fsidBytes\":%zu,"
           "\"flagsOffset\":%zu,\"flagsBytes\":%zu},"
           "\"heldDirectory\":{\"fstatfsResult\":0,\"directory\":true,"
           "\"filesystemTypeHex\":\"0x%" PRIxMAX "\","
           "\"deviceUnsigned\":\"%" PRIuMAX "\",\"deviceHex\":\"0x%" PRIxMAX "\","
           "\"major\":%" PRIuMAX ",\"minor\":%" PRIuMAX ","
           "\"inodeUnsigned\":\"%" PRIuMAX "\"},"
           "\"canaries\":{\"beforeBytes\":%zu,\"afterBytes\":%zu,"
           "\"valueOffset\":%zu,\"afterOffset\":%zu,"
           "\"beforeIntact\":true,\"afterIntact\":true},\"deviceCases\":[",
           devices_ok ? "true" : "false", architecture(), endianness(),
           sizeof(void *), sizeof(long), sizeof(dev_t), _Alignof(dev_t),
           sizeof(struct stat), _Alignof(struct stat),
           offsetof(struct stat, st_dev), sizeof(identity.st_dev),
           offsetof(struct stat, st_ino), sizeof(identity.st_ino),
           offsetof(struct stat, st_mode), sizeof(identity.st_mode),
           offsetof(struct stat, st_nlink), sizeof(identity.st_nlink),
           offsetof(struct stat, st_uid), sizeof(identity.st_uid),
           offsetof(struct stat, st_gid), sizeof(identity.st_gid),
           offsetof(struct stat, st_ctim.tv_sec), sizeof(identity.st_ctim.tv_sec),
           offsetof(struct stat, st_ctim.tv_nsec), sizeof(identity.st_ctim.tv_nsec),
           sizeof(struct statfs), _Alignof(struct statfs),
           offsetof(struct statfs, f_type), sizeof(guarded.value.f_type),
           offsetof(struct statfs, f_fsid), sizeof(guarded.value.f_fsid),
           offsetof(struct statfs, f_flags), sizeof(guarded.value.f_flags),
           (uintmax_t)guarded.value.f_type, (uintmax_t)identity.st_dev,
           (uintmax_t)identity.st_dev, (uintmax_t)major(identity.st_dev),
           (uintmax_t)minor(identity.st_dev), (uintmax_t)identity.st_ino,
           sizeof(guarded.before), sizeof(guarded.after),
           offsetof(struct guarded_statfs, value), offsetof(struct guarded_statfs, after));

    for (size_t i = 0; i < sizeof(device_cases) / sizeof(device_cases[0]); ++i) {
        dev_t encoded = makedev(device_cases[i].major_number, device_cases[i].minor_number);
        printf("%s{\"inputMajor\":%" PRIu32 ",\"inputMinor\":%" PRIu32 ","
               "\"encodedUnsigned\":\"%" PRIuMAX "\",\"encodedHex\":\"0x%" PRIxMAX "\","
               "\"decodedMajor\":%" PRIuMAX ",\"decodedMinor\":%" PRIuMAX "}",
               i == 0 ? "" : ",", device_cases[i].major_number, device_cases[i].minor_number,
               (uintmax_t)encoded, (uintmax_t)encoded,
               (uintmax_t)major(encoded), (uintmax_t)minor(encoded));
    }
    printf("]}\n");
    return devices_ok ? 0 : 1;
}
