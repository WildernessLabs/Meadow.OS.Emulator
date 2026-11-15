/* Minimal LFS config for host (macOS/Linux) builds of build_lfs_v1_image.
 * Replaces NuttX-dependent lfs_util.h via -DLFS_CONFIG=lfs_host_config.h */
#ifndef LFS_HOST_CONFIG_H
#define LFS_HOST_CONFIG_H

#include <stdint.h>
#include <stdbool.h>
#include <string.h>
#include <stdlib.h>

#ifndef LFS_NAME_MAX
#define LFS_NAME_MAX 255
#endif

/* Suppress all logging and assertions for the image builder */
#define LFS_DEBUG(fmt, ...)
#define LFS_WARN(fmt, ...)
#define LFS_ERROR(fmt, ...)
#define LFS_ASSERT(test)

/* FAR is a NuttX pointer annotation — no-op on host */
#ifndef FAR
#define FAR
#endif

#ifndef CODE
#define CODE
#endif

/* NuttX renames upstream structs with _s suffix — map back */
#define lfs_config lfs_config_s

/* Utility inlines (copied from lfs_util.h, no NuttX deps) */

static inline uint32_t lfs_max(uint32_t a, uint32_t b) { return (a > b) ? a : b; }
static inline uint32_t lfs_min(uint32_t a, uint32_t b) { return (a < b) ? a : b; }

static inline uint32_t lfs_npw2(uint32_t a) {
    return 32 - __builtin_clz(a - 1);
}

static inline uint32_t lfs_ctz(uint32_t a) {
    return __builtin_ctz(a);
}

static inline uint32_t lfs_popc(uint32_t a) {
    return __builtin_popcount(a);
}

static inline int lfs_scmp(uint32_t a, uint32_t b) {
    return (int)(unsigned)(a - b);
}

static inline uint32_t lfs_fromle32(uint32_t a) { return a; } /* host is little-endian */
static inline uint32_t lfs_tole32(uint32_t a) { return a; }

static inline void *lfs_malloc(size_t size) { return malloc(size); }
static inline void lfs_free(void *p) { free(p); }

/* CRC-32 with polynomial = 0x04c11db7 (small table) */
static inline void lfs_crc(uint32_t *crc, const void *buffer, size_t size) {
    static const uint32_t rtable[16] = {
        0x00000000, 0x1db71064, 0x3b6e20c8, 0x26d930ac,
        0x76dc4190, 0x6b6b51f4, 0x4db26158, 0x5005713c,
        0xedb88320, 0xf00f9344, 0xd6d6a3e8, 0xcb61b38c,
        0x9b64c2b0, 0x86d3d2d4, 0xa00ae278, 0xbdbdf21c,
    };
    const uint8_t *data = buffer;
    for (size_t i = 0; i < size; i++) {
        *crc = (*crc >> 4) ^ rtable[(*crc ^ (data[i] >> 0)) & 0xf];
        *crc = (*crc >> 4) ^ rtable[(*crc ^ (data[i] >> 4)) & 0xf];
    }
}

#endif
