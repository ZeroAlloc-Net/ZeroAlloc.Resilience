namespace ZeroAlloc.Resilience;

/// <summary>
/// Marks an <see cref="int"/> or <c>int?</c> parameter of a method under <see cref="RetryAttribute"/>
/// that receives the retry number of the current attempt. The generated proxy ignores the caller's
/// argument for it and passes, for <c>int?</c>, <see langword="null"/> on the first attempt and then
/// 1, 2 and so on; for <see cref="int"/>, 0 on the first attempt and then 1, 2 and so on.
/// </summary>
/// <remarks>
/// The number counts the attempts of one call to the proxy, so concurrent calls never share it.
/// Combined with a ZeroAlloc.Rest <c>[Header]</c> parameter, it puts a retry-count header on the
/// wire. The generator reports ZR0011 when the method's interface has resilience attributes but
/// no <see cref="RetryAttribute"/> applies to the method, and ZR0012 when the parameter is not an <see cref="int"/> or <c>int?</c> passed by value.
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class RetryAttemptAttribute : Attribute
{
}
