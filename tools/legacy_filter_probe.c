/*
 * Read-only diagnostic for the supplied 32-bit legacy libvj.dll.
 *
 * It relies on the analysed DLL image layout only to locate the global pointer
 * to the filter table. It does not patch, write, or otherwise alter the DLL.
 */
#define WINVER 0x0501
#define _WIN32_WINNT 0x0501

#include <stdio.h>
#include <windows.h>

enum {
    FILTER_COUNT = 23,
    FILTER_TABLE_POINTER_RVA = 0x69104,
    /* Internal routine that prepares FFT, Hamming, and the MFCC filter table. */
    MFCC_INITIALIZER_RVA = 0x31C40
};

typedef struct FilterRecord {
    int start_bin;
    int end_bin;
    const float *weights;
} FilterRecord;

typedef void (__cdecl *MfccInitializer)(void);

int main(void)
{
    const char *dll_path = "C:\\Program Files\\Vocal Joystick\\vjlib\\lib\\win32\\libvj.dll";
    HMODULE module = LoadLibraryA(dll_path);
    const FilterRecord *filters;
    int filter_index;

    if (module == NULL) {
        fprintf(stderr, "ERROR: LoadLibraryA failed for %s (Win32 error %lu)\n", dll_path, GetLastError());
        return 1;
    }

    filters = *(const FilterRecord **)((const BYTE *)module + FILTER_TABLE_POINTER_RVA);
    if (filters == NULL) {
        /*
         * The table is initialized lazily by the native engine rather than at
         * DLL-load time. This runs that normal initializer only in this short-
         * lived probe process; it cannot persist any change to the DLL file.
         */
        MfccInitializer initialize = (MfccInitializer)((BYTE *)module + MFCC_INITIALIZER_RVA);
        initialize();
        filters = *(const FilterRecord **)((const BYTE *)module + FILTER_TABLE_POINTER_RVA);
    }
    if (filters == NULL) {
        fprintf(stderr, "ERROR: MFCC filter table remained null after its initializer.\n");
        FreeLibrary(module);
        return 2;
    }

    printf("filter_count=%d\n", FILTER_COUNT);
    for (filter_index = 0; filter_index < FILTER_COUNT; filter_index++) {
        const FilterRecord *filter = &filters[filter_index];
        int bin;
        int length = filter->end_bin - filter->start_bin + 1;

        if (filter->weights == NULL || length <= 0 || length > 1024) {
            fprintf(stderr, "ERROR: invalid filter %d: start=%d end=%d weights=%p\n",
                    filter_index, filter->start_bin, filter->end_bin, filter->weights);
            FreeLibrary(module);
            return 3;
        }

        printf("filter %d start=%d end=%d weights=", filter_index, filter->start_bin, filter->end_bin);
        for (bin = 0; bin < length; bin++)
            printf("%s%.9g", bin == 0 ? "" : ",", filter->weights[bin]);
        printf("\n");
    }

    FreeLibrary(module);
    return 0;
}
