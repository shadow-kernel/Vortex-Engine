#pragma once

// Minimal SAL (Microsoft Source Annotation Language) stub for non-Windows builds.
//
// DirectXMath annotates its declarations with SAL macros that the Windows SDK's
// sal.h supplies. On macOS / Linux there is no SDK, so this header defines the
// annotations DirectXMath (and the engine) use as empty. Only the CMake build
// adds this directory to the include path, and only on non-Windows platforms.
// Reference: https://github.com/microsoft/DirectXMath#compiler-support

#ifndef _In_
#define _In_
#endif
#ifndef _In_z_
#define _In_z_
#endif
#ifndef _In_opt_
#define _In_opt_
#endif
#ifndef _In_opt_z_
#define _In_opt_z_
#endif
#ifndef _In_reads_
#define _In_reads_(size)
#endif
#ifndef _In_reads_opt_
#define _In_reads_opt_(size)
#endif
#ifndef _In_reads_bytes_
#define _In_reads_bytes_(size)
#endif
#ifndef _In_reads_bytes_opt_
#define _In_reads_bytes_opt_(size)
#endif
#ifndef _In_reads_z_
#define _In_reads_z_(size)
#endif
#ifndef _Out_
#define _Out_
#endif
#ifndef _Out_z_
#define _Out_z_
#endif
#ifndef _Out_opt_
#define _Out_opt_
#endif
#ifndef _Out_writes_
#define _Out_writes_(size)
#endif
#ifndef _Out_writes_opt_
#define _Out_writes_opt_(size)
#endif
#ifndef _Out_writes_bytes_
#define _Out_writes_bytes_(size)
#endif
#ifndef _Out_writes_bytes_opt_
#define _Out_writes_bytes_opt_(size)
#endif
#ifndef _Out_writes_z_
#define _Out_writes_z_(size)
#endif
#ifndef _Out_writes_to_
#define _Out_writes_to_(size, count)
#endif
#ifndef _Out_writes_all_
#define _Out_writes_all_(size)
#endif
#ifndef _Outptr_
#define _Outptr_
#endif
#ifndef _Outptr_opt_
#define _Outptr_opt_
#endif
#ifndef _Outptr_result_maybenull_
#define _Outptr_result_maybenull_
#endif
#ifndef _Inout_
#define _Inout_
#endif
#ifndef _Inout_z_
#define _Inout_z_
#endif
#ifndef _Inout_opt_
#define _Inout_opt_
#endif
#ifndef _Inout_updates_
#define _Inout_updates_(size)
#endif
#ifndef _Inout_updates_opt_
#define _Inout_updates_opt_(size)
#endif
#ifndef _Inout_updates_bytes_
#define _Inout_updates_bytes_(size)
#endif
#ifndef _Inout_updates_z_
#define _Inout_updates_z_(size)
#endif
#ifndef _Ret_maybenull_
#define _Ret_maybenull_
#endif
#ifndef _Ret_notnull_
#define _Ret_notnull_
#endif
#ifndef _Ret_z_
#define _Ret_z_
#endif
#ifndef _Check_return_
#define _Check_return_
#endif
#ifndef _Must_inspect_result_
#define _Must_inspect_result_
#endif
#ifndef _Success_
#define _Success_(expr)
#endif
#ifndef _Return_type_success_
#define _Return_type_success_(expr)
#endif
#ifndef _Use_decl_annotations_
#define _Use_decl_annotations_
#endif
#ifndef _Analysis_assume_
#define _Analysis_assume_(expr)
#endif
#ifndef _Analysis_noreturn_
#define _Analysis_noreturn_
#endif
#ifndef _Null_terminated_
#define _Null_terminated_
#endif
#ifndef _Field_size_
#define _Field_size_(size)
#endif
#ifndef _Field_size_opt_
#define _Field_size_opt_(size)
#endif
#ifndef _Field_size_bytes_
#define _Field_size_bytes_(size)
#endif
#ifndef _Field_size_bytes_opt_
#define _Field_size_bytes_opt_(size)
#endif
#ifndef _Field_range_
#define _Field_range_(min, max)
#endif
#ifndef _In_range_
#define _In_range_(min, max)
#endif
#ifndef _Out_range_
#define _Out_range_(min, max)
#endif
#ifndef _Pre_
#define _Pre_
#endif
#ifndef _Post_
#define _Post_
#endif
#ifndef _Pre_notnull_
#define _Pre_notnull_
#endif
#ifndef _Pre_maybenull_
#define _Pre_maybenull_
#endif
#ifndef _Post_notnull_
#define _Post_notnull_
#endif
#ifndef _Post_maybenull_
#define _Post_maybenull_
#endif
#ifndef _Deref_out_
#define _Deref_out_
#endif
#ifndef _Deref_out_opt_
#define _Deref_out_opt_
#endif
#ifndef _Reserved_
#define _Reserved_
#endif
#ifndef _When_
#define _When_(cond, annotation)
#endif
#ifndef _At_
#define _At_(target, annotation)
#endif
#ifndef _Printf_format_string_
#define _Printf_format_string_
#endif
#ifndef _Scanf_format_string_
#define _Scanf_format_string_
#endif
#ifndef __analysis_assume
#define __analysis_assume(expr)
#endif
