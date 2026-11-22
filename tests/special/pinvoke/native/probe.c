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

int32_t check_utf16(const uint16_t *value) {
    if (!value) return -1;
    return value[0] == 0x65e5 && value[1] == 0xd83d && value[2] == 0xde00 && value[3] == 0;
}

int32_t check_utf8(const unsigned char *value) {
    if (!value) return -1;
    return value[0] == 0xe6 && value[1] == 0x97 && value[2] == 0xa5 &&
        value[3] == 0xf0 && value[4] == 0x9f && value[5] == 0x98 && value[6] == 0x80 && value[7] == 0;
}
