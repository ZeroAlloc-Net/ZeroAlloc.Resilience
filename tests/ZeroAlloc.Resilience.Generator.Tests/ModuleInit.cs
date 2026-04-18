using System.Runtime.CompilerServices;
using VerifyTests;

namespace ZeroAlloc.Resilience.Generator.Tests;

public static class ModuleInit
{
    [ModuleInitializer]
    public static void Init() => VerifySourceGenerators.Initialize();
}
