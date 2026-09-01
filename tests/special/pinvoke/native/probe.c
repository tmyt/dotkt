#include <stdint.h>
#include <errno.h>

int32_t add_i32(int32_t left, int32_t right) { return left + right; }
void increment_i32(int32_t *value) { ++*value; }
int32_t none_i32(int32_t value) { return value; }
int32_t ansi_i32(int32_t value) { return value; }
int32_t auto_i32(int32_t value) { return value; }
int32_t options_i32(int32_t value) { return value; }
int32_t set_error_i32(int32_t value) { errno = value; return -1; }
int32_t mode_i32(int32_t value) { return value; }
intptr_t intptr_identity(intptr_t value) { return value; }
uintptr_t uintptr_identity(uintptr_t value) { return value; }
