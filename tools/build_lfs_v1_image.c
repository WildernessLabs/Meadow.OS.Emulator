/* Build a LittleFS v1 image for Meadow legacy firmware */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>
#include <dirent.h>
#include <sys/stat.h>
#include <limits.h>
#include "lfs.h"
#include "lfs_util.h"

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

/* Write all files from a directory to a given LFS path prefix */
static int write_dir_to_lfs(lfs_t *lfs, const char *host_dir, const char *lfs_prefix,
                            const char *filter_ext, int *file_count, size_t *total_size) {
    DIR *dir = opendir(host_dir);
    if (!dir) {
        fprintf(stderr, "ERROR: Cannot open directory: %s\n", host_dir);
        return -1;
    }
    struct dirent *entry;
    while ((entry = readdir(dir)) != NULL) {
        if (entry->d_name[0] == '.') continue;
        size_t nlen = strlen(entry->d_name);
        if (filter_ext) {
            size_t elen = strlen(filter_ext);
            if (nlen < elen) continue;
            if (strcasecmp(entry->d_name + nlen - elen, filter_ext) != 0) continue;
        }
        char src_path[1024];
        snprintf(src_path, sizeof(src_path), "%s/%s", host_dir, entry->d_name);
        struct stat st;
        if (stat(src_path, &st) != 0 || !S_ISREG(st.st_mode)) continue;
        uint8_t *data = malloc(st.st_size);
        if (!data) { fprintf(stderr, "ERROR: malloc failed for %s\n", entry->d_name); continue; }
        FILE *fp = fopen(src_path, "rb");
        if (!fp) { fprintf(stderr, "WARNING: Cannot open %s\n", src_path); free(data); continue; }
        fread(data, 1, st.st_size, fp);
        fclose(fp);
        char lfs_path[512];
        if (lfs_prefix[0])
            snprintf(lfs_path, sizeof(lfs_path), "%s/%s", lfs_prefix, entry->d_name);
        else
            snprintf(lfs_path, sizeof(lfs_path), "%s", entry->d_name);
        lfs_file_t file;
        int err = lfs_file_open(lfs, &file, lfs_path, LFS_O_WRONLY | LFS_O_CREAT);
        if (err) { fprintf(stderr, "ERROR: open %s failed: %d\n", lfs_path, err); free(data); continue; }
        lfs_file_write(lfs, &file, data, st.st_size);
        lfs_file_close(lfs, &file);
        printf("  %s: %lld bytes\n", lfs_path, (long long)st.st_size);
        if (total_size) *total_size += st.st_size;
        if (file_count) (*file_count)++;
        free(data);
    }
    closedir(dir);
    return 0;
}

int main(int argc, char *argv[]) {
    if (argc < 3) {
        fprintf(stderr, "Usage: %s <bcl_assembly_dir> <output.bin> [app_dir]\n", argv[0]);
        fprintf(stderr, "  bcl_assembly_dir: BCL DLLs → / (root)\n");
        fprintf(stderr, "  app_dir:          App files → / (root, all file types)\n");
        return 1;
    }

    const char *asm_dir = argv[1];
    const char *output_path = argv[2];
    const char *app_dir = (argc >= 4) ? argv[3] : NULL;

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
        .lookahead      = 32,
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

    /* Write BCL assemblies to / (root) — firmware checks /meadow0/mscorlib.dll etc. */
    int file_count = 0;
    size_t total_size = 0;
    printf("BCL assemblies → / (root):\n");
    write_dir_to_lfs(&lfs, asm_dir, "", ".dll", &file_count, &total_size);
    printf("  %d BCL assemblies (%zu KB)\n\n", file_count, total_size / 1024);

    /* Write app files to root if app_dir specified */
    if (app_dir) {
        int app_count = 0;
        size_t app_size = 0;
        printf("App files → / (root):\n");
        write_dir_to_lfs(&lfs, app_dir, "", NULL, &app_count, &app_size);
        printf("  %d app files (%zu KB)\n\n", app_count, app_size / 1024);
    }

    /* Write meadow.config.yaml at filesystem root */
    static const char config_yaml[] =
        "MonoControl:\n"
        "    Options:\n";

    lfs_file_t cfgfile;
    err = lfs_file_open(&lfs, &cfgfile, "meadow.config.yaml", LFS_O_WRONLY | LFS_O_CREAT);
    if (err) {
        fprintf(stderr, "ERROR: open meadow.config.yaml failed: %d\n", err);
    } else {
        lfs_file_write(&lfs, &cfgfile, config_yaml, strlen(config_yaml));
        lfs_file_close(&lfs, &cfgfile);
        printf("  meadow.config.yaml: %zu bytes (MonoControl: JIT)\n", strlen(config_yaml));
    }

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
