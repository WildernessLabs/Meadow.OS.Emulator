/* Build a LittleFS v1 image for Meadow legacy firmware */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <dirent.h>
#include <sys/stat.h>
#include "lfs.h"

#define FS_BLOCK_SIZE   4096
#define FS_READ_SIZE    256
#define FS_PROG_SIZE    256
#define FS_BLOCK_COUNT  14336

static uint8_t *flash_image;

static int ram_read(const struct lfs_config *c, lfs_block_t block,
                    lfs_off_t off, void *buffer, lfs_size_t size) {
    memcpy(buffer, flash_image + block * c->block_size + off, size);
    return 0;
}
static int ram_prog(const struct lfs_config *c, lfs_block_t block,
                    lfs_off_t off, const void *buffer, lfs_size_t size) {
    memcpy(flash_image + block * c->block_size + off, buffer, size);
    return 0;
}
static int ram_erase(const struct lfs_config *c, lfs_block_t block) {
    memset(flash_image + block * c->block_size, 0xFF, c->block_size);
    return 0;
}
static int ram_sync(const struct lfs_config *c) { return 0; }

int main(int argc, char *argv[]) {
    if (argc < 3) {
        fprintf(stderr, "Usage: %s <assembly_dir> <output.bin>\n", argv[0]);
        return 1;
    }

    const char *asm_dir = argv[1];
    const char *output_path = argv[2];

    size_t image_size = (size_t)FS_BLOCK_SIZE * FS_BLOCK_COUNT;
    flash_image = malloc(image_size);
    if (!flash_image) {
        fprintf(stderr, "ERROR: Failed to allocate %zu bytes\n", image_size);
        return 1;
    }
    memset(flash_image, 0xFF, image_size);

    struct lfs_config cfg = {
        .read  = ram_read,
        .prog  = ram_prog,
        .erase = ram_erase,
        .sync  = ram_sync,
        .read_size      = FS_READ_SIZE,
        .prog_size      = FS_PROG_SIZE,
        .block_size     = FS_BLOCK_SIZE,
        .block_count    = FS_BLOCK_COUNT,
        .lookahead      = 8,
    };

    lfs_t lfs;
    int err;

    err = lfs_format(&lfs, &cfg);
    if (err) {
        fprintf(stderr, "ERROR: lfs_format failed: %d\n", err);
        free(flash_image);
        return 1;
    }

    err = lfs_mount(&lfs, &cfg);
    if (err) {
        fprintf(stderr, "ERROR: lfs_mount failed: %d\n", err);
        free(flash_image);
        return 1;
    }

    /* Create /mono/4.5 directory */
    err = lfs_mkdir(&lfs, "mono");
    if (err && err != LFS_ERR_EXIST) {
        fprintf(stderr, "ERROR: mkdir mono failed: %d\n", err);
        lfs_unmount(&lfs);
        free(flash_image);
        return 1;
    }
    err = lfs_mkdir(&lfs, "mono/4.5");
    if (err && err != LFS_ERR_EXIST) {
        fprintf(stderr, "ERROR: mkdir mono/4.5 failed: %d\n", err);
        lfs_unmount(&lfs);
        free(flash_image);
        return 1;
    }

    /* Scan assembly directory for DLLs */
    DIR *dir = opendir(asm_dir);
    if (!dir) {
        fprintf(stderr, "ERROR: Cannot open directory: %s\n", asm_dir);
        lfs_unmount(&lfs);
        free(flash_image);
        return 1;
    }

    int file_count = 0;
    size_t total_size = 0;
    struct dirent *entry;

    while ((entry = readdir(dir)) != NULL) {
        size_t nlen = strlen(entry->d_name);
        if (nlen < 4) continue;
        if (strcasecmp(entry->d_name + nlen - 4, ".dll") != 0) continue;

        char src_path[1024];
        snprintf(src_path, sizeof(src_path), "%s/%s", asm_dir, entry->d_name);

        struct stat st;
        if (stat(src_path, &st) != 0) {
            fprintf(stderr, "WARNING: Cannot stat %s\n", src_path);
            continue;
        }

        uint8_t *data = malloc(st.st_size);
        if (!data) {
            fprintf(stderr, "ERROR: malloc failed for %s\n", entry->d_name);
            continue;
        }

        FILE *fp = fopen(src_path, "rb");
        if (!fp) {
            fprintf(stderr, "WARNING: Cannot open %s\n", src_path);
            free(data);
            continue;
        }
        fread(data, 1, st.st_size, fp);
        fclose(fp);

        /* Write to /mono/4.5/ for Mono runtime */
        char lfs_path[512];
        snprintf(lfs_path, sizeof(lfs_path), "mono/4.5/%s", entry->d_name);

        lfs_file_t file;
        err = lfs_file_open(&lfs, &file, lfs_path, LFS_O_WRONLY | LFS_O_CREAT);
        if (err) {
            fprintf(stderr, "ERROR: open %s failed: %d\n", lfs_path, err);
            free(data);
            continue;
        }
        lfs_file_write(&lfs, &file, data, st.st_size);
        lfs_file_close(&lfs, &file);

        printf("  %s: %lld bytes\n", entry->d_name, (long long)st.st_size);
        total_size += st.st_size;
        file_count++;
        free(data);
    }
    closedir(dir);

    printf("Found %d assemblies (%zu KB total)\n", file_count, total_size / 1024);
    lfs_unmount(&lfs);

    /* Write image to file */
    FILE *out = fopen(output_path, "wb");
    if (!out) {
        fprintf(stderr, "ERROR: Cannot create %s\n", output_path);
        free(flash_image);
        return 1;
    }
    fwrite(flash_image, 1, image_size, out);
    fclose(out);

    printf("\nWrote %zu bytes to %s\n", image_size, output_path);
    printf("Block size: %d, Block count: %d\n", FS_BLOCK_SIZE, FS_BLOCK_COUNT);

    free(flash_image);
    return 0;
}
