/*
 * Host stand-in for NuttX's fs_heap.h, for building Meadow.OS's (NuttX-patched)
 * LittleFS v2 lfs_util.h on the host (tools/build_lfs_v2_image.c).  The NuttX
 * patch allocates from the file system heap; on the host that is the C heap.
 */
#ifndef LFS_V2_HOST_FS_HEAP_H
#define LFS_V2_HOST_FS_HEAP_H
#include <stdlib.h>
#define fs_heap_malloc(size) malloc(size)
#define fs_heap_free(p)      free(p)
#endif
