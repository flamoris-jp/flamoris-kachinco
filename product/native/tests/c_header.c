#include "kachinco_native.h"
/* Compile and link the actual public header as C, not merely as C++. */
int kn_c_header_check(void) {
    return kn_abi_version() == KN_ABI_VERSION && sizeof(kn_runtime_info) == 24 && sizeof(kn_media_value) == 32;
}
