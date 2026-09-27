; Shipped analyzer releases.
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md
; ZR0005 is absent: it was added and removed on the #168 branch before merge, so no release
; ever contained it. The id stays retired and must not be reused.

## Release 1.0.0

### New Rules

Rule ID | Category             | Severity | Notes
--------|----------------------|----------|-------------------------------------------------------
ZR0001  | ZeroAlloc.Resilience | Error    | Fallback method not found or signature mismatch
ZR0002  | ZeroAlloc.Resilience | Warning  | Timeout configured but method has no CancellationToken

## Release 1.3.6

### New Rules

Rule ID | Category             | Severity | Notes
--------|----------------------|----------|---------------------------------------------------------
ZR0003  | ZeroAlloc.Resilience | Error    | Policy cannot build a failure for this Result error type

## Release 2.0.0

### New Rules

Rule ID | Category             | Severity | Notes
--------|----------------------|----------|-------------------------------
ZR0004  | ZeroAlloc.Resilience | Error    | Invalid policy attribute value

## Release 3.0.0

### New Rules

Rule ID | Category             | Severity | Notes
--------|----------------------|----------|----------------------------------------------------------
ZR0006  | ZeroAlloc.Resilience | Warning  | Policy not applied to method
ZR0007  | ZeroAlloc.Resilience | Error    | Interface shape not supported by the resilience generator

## Release 3.1.0

### New Rules

Rule ID | Category             | Severity | Notes
--------|----------------------|----------|---------------------------------------------
ZR0008  | ZeroAlloc.Resilience | Error    | Invalid ZeroAllocGeneratedAccessibility value

## Release 3.2.0

### New Rules

Rule ID | Category             | Severity | Notes
--------|----------------------|----------|----------------------------------------------
ZR0009  | ZeroAlloc.Resilience | Error    | Retry member not found or signature mismatch
ZR0010  | ZeroAlloc.Resilience | Error    | Result-aware retry cannot apply to method
