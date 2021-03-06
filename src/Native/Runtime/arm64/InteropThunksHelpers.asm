;; Licensed to the .NET Foundation under one or more agreements.
;; The .NET Foundation licenses this file to you under the MIT license.
;; See the LICENSE file in the project root for more information.


#include "ksarm64.h"

;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;  DATA SECTIONS  ;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;

__tls_array                         equ 0x58    ;; offsetof(TEB, ThreadLocalStoragePointer)

POINTER_SIZE                        equ 0x08

;; TLS variables
    AREA    |.tls$|, DATA
ThunkParamSlot % 0x8

    TEXTAREA

    EXTERN _tls_index

;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;; Interop Thunks Helpers ;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;

    ;;
    ;; RhCommonStub
    ;;
    ;;  INPUT: xip0: thunk's data block
    ;;
    ;;  TRASHES: x9, x10, x11, xip0
    ;;
    LEAF_ENTRY RhCommonStub
        ;; There are arbitrary callers passing arguments with arbitrary signatures.
        ;; Custom calling convention:
        ;;      xip0 pointer to the current thunk's data block (data contains 2 pointer values: context + target pointers)
        
        brk 0xf000      ;; TODO: remove after debugging/testing stub

        ;; Save context data into the ThunkParamSlot thread-local variable
        ;; A pointer to the delegate and function pointer for open static delegate should have been saved in the thunk's context cell during thunk allocation
        ldr         x10, =_tls_index
        ldr         x10, [x10]
        ldr         x9, [xpr, #__tls_array]
        ldr         x9, [x9, x10 lsl #3]     ;; x9 <- our TLS base
       
        ;; x9  = base address of TLS data
        ;; x10 = trashed
        ;; xip0 = address of context cell in thunk's data

        ;; store thunk address in thread static
        ldr         x10, [xip0]
        ldr         x11, =ThunkParamSlot
        str         x10, [x9, x11]            ;; ThunkParamSlot <- context slot data
        
        ;; Now load the target address and jump to it.
        ldr         xip0, [xip0, #POINTER_SIZE]
        ret         xip0

    LEAF_END RhCommonStub

    ;;
    ;; IntPtr RhGetCommonStubAddress()
    ;;
    LEAF_ENTRY RhGetCommonStubAddress
        ldr     x0, =RhCommonStub
        ret
    LEAF_END RhGetCommonStubAddress


    ;;
    ;; IntPtr RhGetCurrentThunkContext()
    ;;
    LEAF_ENTRY RhGetCurrentThunkContext
        brk 0xf000      ;; TODO: remove after debugging/testing stub

        ldr         x1, =_tls_index
        ldr         x1, [x1]
        ldr         x0, [xpr, #__tls_array]
        ldr         x0, [x0, x1 lsl #3]     ;; x0 <- our TLS base

        ldr         x1, =ThunkParamSlot
        ldr         x0, [x0, x1]            ;; x0 <- ThunkParamSlot

        ret

    LEAF_END RhGetCurrentThunkContext

    END
