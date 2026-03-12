# Upstream Fixes

Tracking fixes made to upstream dependencies that should be PR'd back to their respective projects.

## Renode ([renode/renode](https://github.com/renode/renode))

### 1. Xtensa `arch_tlb_fill` returns wrong error code (SYSCALL_CAUSE misreport)

| Field | Value |
|-------|-------|
| **Status** | Fixed locally, not yet PR'd |
| **Repo** | `bryancostanich/renode` (fork) |
| **Worktree** | `renode-retw-fix` |
| **Branch** | `fix/xtensa-retw` |
| **Commit** | Not yet committed |
| **File(s)** | `src/Infrastructure/src/Emulator/Cores/tlib/arch/xtensa/helper.c` |
| **Severity** | Critical |

**Problem:** `arch_tlb_fill()` discards the real MMU exception cause code from `get_physical_address()` and returns `TRANSLATE_FAIL` (which is `1`). The `TRY_FILL` macro in `exec.c` passes this value to `arch_raise_mmu_fault_exception()`, which forwards it as the exception cause to `HELPER(exception_cause_vaddr)`. Since `SYSCALL_CAUSE` is also `1`, every MMU fault (TLB miss, store prohibited, etc.) is misreported as a SYSCALL exception.

On ESP32, the `_xt_syscall_exc` handler adds 3 to EPC1 (to skip the 3-byte SYSCALL instruction). When the real instruction is a 2-byte narrow instruction like `s32i.n`, this corrupts EPC1 to a non-instruction boundary, causing cascading crashes.

**Fix:** Changed `return TRANSLATE_FAIL;` to `return ret;` so the actual Xtensa exception cause code (e.g., `STORE_PROHIBITED_CAUSE=29`) is propagated correctly. All Xtensa cause codes are non-zero, so the `TRY_FILL` error check (`errcode != TRANSLATE_SUCCESS`) still works.

**Impact:** Fixes PC misalignment crashes in any Xtensa firmware that uses narrow (2-byte) instructions near code that triggers MMU faults. Particularly affects FreeRTOS `vListInsertEnd`/`vListInsert` on ESP32.

---

### 2. (Placeholder) Boot script workarounds still in use

| Field | Value |
|-------|-------|
| **Status** | Under investigation |
| **Notes** | Several workarounds in the boot script (RETW patches, IPC task priority demotion, list fixups) may mask additional tlib bugs. Need to evaluate which are still needed after fix #1. |

---

## PR Workflow

1. **Fixed locally** — code change made and tested in this repo
2. **PR submitted** — PR opened upstream
3. **Accepted** — merged into upstream renode
