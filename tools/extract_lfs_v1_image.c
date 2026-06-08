/* Extract files from a LittleFS v1 image to a host directory */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include "lfs.h"

#define FS_BLOCK_SIZE   4096
#define FS_READ_SIZE    256
#define FS_PROG_SIZE    256
#define FS_BLOCK_COUNT  14336

static uint8_t *flash_image;

static int flash_read(const struct lfs_config *c, lfs_block_t block, lfs_off_t off, void *buffer, lfs_size_t size) {
    memcpy(buffer, flash_image + block * c->block_size + off, size);
    return 0;
}
static int flash_prog(const struct lfs_config *c, lfs_block_t block, lfs_off_t off, const void *buffer, lfs_size_t size) {
    return 0; /* read-only */
}
static int flash_erase(const struct lfs_config *c, lfs_block_t block) { return 0; }
static int flash_sync(const struct lfs_config *c) { return 0; }

static void extract_dir(lfs_t *lfs, const char *lfs_path, const char *host_dir) {
    lfs_dir_t dir;
    struct lfs_info info;
    int err = lfs_dir_open(lfs, &dir, lfs_path);
    if (err) { fprintf(stderr, "ERROR: Cannot open dir '%s': %d\n", lfs_path, err); return; }

    while (lfs_dir_read(lfs, &dir, &info) > 0) {
        if (strcmp(info.name, ".") == 0 || strcmp(info.name, "..") == 0) continue;

        char lfs_full[512], host_full[512];
        if (lfs_path[0] == '/' && lfs_path[1] == '\0')
            snprintf(lfs_full, sizeof(lfs_full), "/%s", info.name);
        else
            snprintf(lfs_full, sizeof(lfs_full), "%s/%s", lfs_path, info.name);
        snprintf(host_full, sizeof(host_full), "%s/%s", host_dir, info.name);

        if (info.type == LFS_TYPE_DIR) {
            mkdir(host_full, 0755);
            extract_dir(lfs, lfs_full, host_full);
        } else {
            lfs_file_t file;
            err = lfs_file_open(lfs, &file, lfs_full, LFS_O_RDONLY);
            if (err) { fprintf(stderr, "ERROR: Cannot open '%s': %d\n", lfs_full, err); continue; }
            lfs_ssize_t size = lfs_file_size(lfs, &file);
            uint8_t *data = malloc(size);
            lfs_file_read(lfs, &file, data, size);
            lfs_file_close(lfs, &file);

            FILE *fp = fopen(host_full, "wb");
            if (!fp) { fprintf(stderr, "ERROR: Cannot create '%s'\n", host_full); free(data); continue; }
            fwrite(data, 1, size, fp);
            fclose(fp);
            printf("  %s (%d bytes)\n", lfs_full, (int)size);
            free(data);
        }
    }
    lfs_dir_close(lfs, &dir);
}

int main(int argc, char *argv[]) {
    if (argc < 3) {
        fprintf(stderr, "Usage: %s <input.bin> <output_dir>\n", argv[0]);
        return 1;
    }

    size_t image_size = (size_t)FS_BLOCK_SIZE * FS_BLOCK_COUNT;
    flash_image = malloc(image_size);
    memset(flash_image, 0xFF, image_size);

    FILE *fp = fopen(argv[1], "rb");
    if (!fp) { fprintf(stderr, "ERROR: Cannot open %s\n", argv[1]); return 1; }
    fread(flash_image, 1, image_size, fp);
    fclose(fp);

    mkdir(argv[2], 0755);

    struct lfs_config cfg = {
        .read = flash_read, .prog = flash_prog, .erase = flash_erase, .sync = flash_sync,
        .read_size = FS_READ_SIZE, .prog_size = FS_PROG_SIZE,
        .block_size = FS_BLOCK_SIZE, .block_count = FS_BLOCK_COUNT,
        .lookahead = 32
    };
    lfs_t lfs;
    int err = lfs_mount(&lfs, &cfg);
    if (err) { fprintf(stderr, "ERROR: mount failed: %d\n", err); free(flash_image); return 1; }

    printf("Extracting to %s:\n", argv[2]);
    extract_dir(&lfs, "/", argv[2]);

    lfs_unmount(&lfs);
    free(flash_image);
    printf("Done.\n");
    return 0;
}
