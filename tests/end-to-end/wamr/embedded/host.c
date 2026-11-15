#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "wasm_export.h"

static int pin_state[64];
static int gpio_set_level(wasm_exec_env_t env, int pin, int level) { pin_state[pin] = level; printf("[host] gpio %d=%d\n", pin, level); return 0; }

static uint8_t *load(const char *p, uint32_t *n) {
    FILE *f = fopen(p, "rb"); fseek(f, 0, SEEK_END); *n = ftell(f); rewind(f);
    uint8_t *b = malloc(*n); fread(b, 1, *n, f); fclose(f); return b;
}
#define CHECK(c, m) do { if (!(c)) { printf("FAIL %s: %s\n", m, err); return 1; } } while (0)

int main(int argc, char **argv) {
    char err[256] = {0};
    static NativeSymbol syms[] = { { "gpio_set_level", gpio_set_level, "(ii)i", NULL } };
    RuntimeInitArgs a; memset(&a, 0, sizeof a);
    a.mem_alloc_type = Alloc_With_System_Allocator;
    a.native_module_name = "esp"; a.native_symbols = syms; a.n_native_symbols = 1;
    CHECK(wasm_runtime_full_init(&a), "init");
    uint32_t n; uint8_t *buf = load(argv[1], &n);
    wasm_module_t m = wasm_runtime_load(buf, n, err, sizeof err); CHECK(m, "load");
    wasm_module_inst_t inst = wasm_runtime_instantiate(m, 64 * 1024, 64 * 1024, err, sizeof err); CHECK(inst, "instantiate");
    wasm_exec_env_t env = wasm_runtime_create_exec_env(inst, 64 * 1024); CHECK(env, "env");
    wasm_function_inst_t f;
    uint32_t v[4];
#define CALL(name, nargs, nres) f = wasm_runtime_lookup_function(inst, name); CHECK(f, "lookup " name); \
    if (!wasm_runtime_call_wasm(env, f, nargs, v)) { printf("FAIL call %s: %s\n", name, wasm_runtime_get_exception(inst)); return 1; }
    CALL("_initialize", 0, 0);
    v[0] = 40; v[1] = 2; CALL("add", 2, 1); printf("add=%d\n", v[0]);
    v[0] = 5; CALL("blink", 1, 1); v[0] = 5; CALL("blink", 1, 1);
    /* callback: wasm function table index 'add' wrapper; use a table slot discovered via the exported call_indirect */
    CALL("get_handler", 0, 1); uint32_t idx = v[0]; printf("handler index=%u\n", idx);
    v[0] = idx; CALL("set_callback", 1, 0);
    v[0] = 20; CALL("fire", 1, 1); printf("fire=%d (expect 41)\n", v[0]);
    v[0] = 7; v[1] = 0; CALL("divide", 2, 1); printf("divide=%d (expect -1)\n", (int)v[0]);
    uint32_t cv[1] = { 21 };
    if (!wasm_runtime_call_indirect(env, idx, 1, cv)) printf("FAIL call_indirect: %s\n", wasm_runtime_get_exception(inst));
    else printf("call_indirect=%d (expect 42)\n", (int)cv[0]);
    printf("done\n");
    return 0;
}
