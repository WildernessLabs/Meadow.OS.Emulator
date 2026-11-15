/*
 * Build a LittleFS v2 image of the Meadow.OS (Rebase, NuttX 12) file system
 * partition, for run.sh --mount.  Normally run through tools/make-lfs-image.sh.
 *
 * Usage: build_lfs_v2_image <app_dir> <output.bin>
 *   app_dir:    files to put at the root of the file system (/meadow0)
 *   output.bin: the image of the whole partition (61,865,984 bytes)
 *
 * Build against Meadow.OS's own (NuttX-patched) LittleFS v2.5 sources, so the
 * on-disk format is exactly what the firmware mounts:
 *   cc -O2 -I<os>/nuttx/fs/littlefs/littlefs -o build_lfs_v2_image \
 *      build_lfs_v2_image.c <os>/nuttx/fs/littlefs/littlefs/lfs.c \
 *      <os>/nuttx/fs/littlefs/littlefs/lfs_util.c \
 *      -DLFS_NO_DEBUG -DLFS_NO_WARN -DLFS_NO_ERROR
 *
 * The geometry is the struct lfs_config the firmware passes to lfs_mount(),
 * measured in the emulator (a hook on lfs_mount, 2026-11-15).  NuttX's
 * lfs_vfs.c derives it from the W25Q MTD geometry (256 byte pages, 4 KB erase
 * blocks) and CONFIG_FS_LITTLEFS_*_FACTOR = 4:
 *   read_size=1024 prog_size=1024 block_size=4096 block_count=15104
 *   block_cycles=200 cache_size=1024 lookahead_size=1024
 *   name_max/file_max/attr_max=0, i.e. the library defaults (255 /
 *   2147483647 / 1022), which this host build also uses.
 * If an image does not match, the firmware fails to mount it and formats the
 * partition (hcom_nx_fs_littlefs.c), so the files are silently lost.
 *
 * If app_dir has no meadow.config.yaml a default one is added
 * (MonoControl: --interp); an app's own config is kept.
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <dirent.h>
#include <sys/stat.h>
#include "lfs.h"

#define FS_READ_SIZE      1024
#define FS_PROG_SIZE      1024
#define FS_BLOCK_SIZE     4096
#define FS_BLOCK_COUNT    15104   /* 61865984-byte partition at QSPI 0x500000 */
#define FS_BLOCK_CYCLES   200
#define FS_CACHE_SIZE     1024
#define FS_LOOKAHEAD_SIZE 1024

static uint8_t *flash_image;

static int ram_read(const struct lfs_config *c, lfs_block_t block,
                    lfs_off_t off, void *buffer, lfs_size_t size) {
    memcpy(buffer, flash_image + (size_t)block * c->block_size + off, size);
    return 0;
}
static int ram_prog(const struct lfs_config *c, lfs_block_t block,
                    lfs_off_t off, const void *buffer, lfs_size_t size) {
    memcpy(flash_image + (size_t)block * c->block_size + off, buffer, size);
    return 0;
}
static int ram_erase(const struct lfs_config *c, lfs_block_t block) {
    memset(flash_image + (size_t)block * c->block_size, 0xFF, c->block_size);
    return 0;
}
static int ram_sync(const struct lfs_config *c) { (void)c; return 0; }

/* Copy every regular file in host_dir to the root of the file system */
static int write_dir_to_root(lfs_t *lfs, const char *host_dir,
                             int *file_count, size_t *total_size) {
    DIR *dir = opendir(host_dir);
    if (!dir) {
        fprintf(stderr, "ERROR: Cannot open directory: %s\n", host_dir);
        return -1;
    }
    int status = 0;
    struct dirent *entry;
    while ((entry = readdir(dir)) != NULL) {
        if (entry->d_name[0] == '.') continue;
        char src_path[1024];
        snprintf(src_path, sizeof(src_path), "%s/%s", host_dir, entry->d_name);
        struct stat st;
        if (stat(src_path, &st) != 0 || !S_ISREG(st.st_mode)) continue;

        uint8_t *data = malloc(st.st_size ? st.st_size : 1);
        FILE *fp = data ? fopen(src_path, "rb") : NULL;
        if (!fp || fread(data, 1, st.st_size, fp) != (size_t)st.st_size) {
            fprintf(stderr, "ERROR: cannot read %s\n", src_path);
            if (fp) fclose(fp);
            free(data);
            status = -1;
            continue;
        }
        fclose(fp);

        lfs_file_t file;
        int err = lfs_file_open(lfs, &file, entry->d_name,
                                LFS_O_WRONLY | LFS_O_CREAT | LFS_O_TRUNC);
        if (!err) {
            lfs_ssize_t n = lfs_file_write(lfs, &file, data, st.st_size);
            err = lfs_file_close(lfs, &file);
            if (n != (lfs_ssize_t)st.st_size) err = (n < 0) ? (int)n : LFS_ERR_NOSPC;
        }
        free(data);
        if (err) {
            fprintf(stderr, "ERROR: writing %s failed: %d\n", entry->d_name, err);
            status = -1;
            continue;
        }
        printf("  %s: %lld bytes\n", entry->d_name, (long long)st.st_size);
        *total_size += st.st_size;
        (*file_count)++;
    }
    closedir(dir);
    return status;
}

int main(int argc, char *argv[]) {
    if (argc != 3) {
        fprintf(stderr, "Usage: %s <app_dir> <output.bin>\n", argv[0]);
        fprintf(stderr, "  app_dir: files to put at the root of /meadow0\n");
        return 1;
    }
    const char *app_dir = argv[1];
    const char *output_path = argv[2];

    size_t image_size = (size_t)FS_BLOCK_SIZE * FS_BLOCK_COUNT;
    flash_image = malloc(image_size);
    if (!flash_image) {
        fprintf(stderr, "ERROR: Failed to allocate %zu bytes\n", image_size);
        return 1;
    }
    memset(flash_image, 0xFF, image_size);   /* erased NOR flash */

    struct lfs_config cfg = {
        .read  = ram_read,
        .prog  = ram_prog,
        .erase = ram_erase,
        .sync  = ram_sync,
        .read_size      = FS_READ_SIZE,
        .prog_size      = FS_PROG_SIZE,
        .block_size     = FS_BLOCK_SIZE,
        .block_count    = FS_BLOCK_COUNT,
        .block_cycles   = FS_BLOCK_CYCLES,
        .cache_size     = FS_CACHE_SIZE,
        .lookahead_size = FS_LOOKAHEAD_SIZE,
        /* name_max, file_max, attr_max: 0 = library defaults, as the firmware */
    };

    lfs_t lfs;
    int err = lfs_format(&lfs, &cfg);
    if (!err) err = lfs_mount(&lfs, &cfg);
    if (err) {
        fprintf(stderr, "ERROR: lfs_format/lfs_mount failed: %d\n", err);
        free(flash_image);
        return 1;
    }

    int file_count = 0;
    size_t total_size = 0;
    printf("App files -> / (root):\n");
    int status = write_dir_to_root(&lfs, app_dir, &file_count, &total_size);
    printf("  %d files (%zu KB)\n\n", file_count, total_size / 1024);

    /* A default meadow.config.yaml only if the app did not supply one */
    lfs_file_t cfgfile;
    if (lfs_file_open(&lfs, &cfgfile, "meadow.config.yaml", LFS_O_RDONLY) == 0) {
        printf("  meadow.config.yaml: kept the app's own (%ld bytes)\n",
               (long)lfs_file_size(&lfs, &cfgfile));
        lfs_file_close(&lfs, &cfgfile);
    } else {
        static const char config_yaml[] =
            "MonoControl:\n"
            "    Options: --interp\n";
        err = lfs_file_open(&lfs, &cfgfile, "meadow.config.yaml",
                            LFS_O_WRONLY | LFS_O_CREAT | LFS_O_TRUNC);
        if (err) {
            fprintf(stderr, "ERROR: open meadow.config.yaml failed: %d\n", err);
            status = -1;
        } else {
            lfs_file_write(&lfs, &cfgfile, config_yaml, strlen(config_yaml));
            lfs_file_close(&lfs, &cfgfile);
            printf("  meadow.config.yaml: %zu bytes (default, MonoControl: --interp)\n",
                   strlen(config_yaml));
        }
    }

    lfs_unmount(&lfs);

    if (status != 0) {
        fprintf(stderr, "ERROR: not all files were written; no image produced\n");
        free(flash_image);
        return 1;
    }

    FILE *out = fopen(output_path, "wb");
    if (!out || fwrite(flash_image, 1, image_size, out) != image_size) {
        fprintf(stderr, "ERROR: Cannot write %s\n", output_path);
        if (out) fclose(out);
        free(flash_image);
        return 1;
    }
    fclose(out);
    free(flash_image);

    printf("\nWrote %zu bytes to %s\n", image_size, output_path);
    printf("LittleFS v2: block size %d, block count %d, read/prog %d/%d\n",
           FS_BLOCK_SIZE, FS_BLOCK_COUNT, FS_READ_SIZE, FS_PROG_SIZE);
    return 0;
}
