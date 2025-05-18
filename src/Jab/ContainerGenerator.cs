using System.Threading;
namespace Jab;

[Generator]
#pragma warning disable RS1001 // We don't want this to be discovered as analyzer but it simplifies testing
public partial class ContainerGenerator : DiagnosticAnalyzer
#pragma warning restore RS1001 // We don't want this to be discovered as analyzer but it simplifies testing
{
    /// <summary>Code for a [GeneratedCode] attribute to put on the top-level generated members.</summary>
    private static readonly string _generatedCodeAttribute = $"[global::System.CodeDom.Compiler.GeneratedCodeAttribute(\"{typeof(ContainerGenerator).Assembly.GetName().Name}\", \"{typeof(ContainerGenerator).Assembly.GetName().Version}\")]";

    private void GenerateCallSiteWithCache(CodeWriter codeWriter, ServiceProvider root, string rootReference, ServiceCallSite serviceCallSite, Action<CodeWriter, CodeWriterDelegate> valueCallback)
    {
        if (serviceCallSite is ErrorCallSite errorCallSite)
        {
            codeWriter.Line($"// There was an error while building the container, please refer to the compiler diagnostics");
            foreach (var diagnostic in errorCallSite.Diagnostic)
            {
                codeWriter.Line($"// {diagnostic.ToString()}");
            }
            codeWriter.Line($"return default!;");
            return;
        }

        if (serviceCallSite.Lifetime != ServiceLifetime.Transient)
        {
            var cacheLocation = GetCacheLocation(serviceCallSite.Identity);
            if (serviceCallSite.ImplementationType.IsValueType)
            {
                codeWriter.Line($"if ({cacheLocation} == null)");
                codeWriter.Line($"lock (this)");
                using (codeWriter.Scope($"if ({cacheLocation} == null)"))
                {
                    GenerateCallSite(
                        codeWriter,
                        rootReference,
                        serviceCallSite,
                        (w, v) =>
                        {
                            w.Line($"{cacheLocation} = {v};");
                        });
                }
                valueCallback(codeWriter, w => w.Append($"{cacheLocation}.Value"));
            }
            else
            {
                codeWriter.Line($"#nullable disable");
                GenerateCallSite(
                    codeWriter,
                    rootReference,
                    serviceCallSite,
                    (w, v) =>
                    {
                        codeWriter.Line($"{typeof(LazyInitializer)}.EnsureInitialized(ref {cacheLocation} , () => {v});");
                    });
                valueCallback(codeWriter, w => w.Append($"{cacheLocation}"));
                codeWriter.Line($"#nullable enable");
            }
        }
        else if (serviceCallSite.IsDisposable != false)
        {
            GenerateCallSite(codeWriter, rootReference, serviceCallSite, (w, v) =>
            {
                w.Line($"{serviceCallSite.ImplementationType} service = {v};");
            });

            var disposableTypes = serviceCallSite.ImplementationType.Interfaces
                .Select(x => x.ToDisplayString())
                .Where(x => x == typeof(IDisposable).FullName || x == root.KnownTypes.IAsyncDisposableType?.ToDisplayString())
                .ToList();

            if (disposableTypes.Count > 0)
            {
                var hasDisposable = disposableTypes.Contains(typeof(IDisposable).FullName);
                if (root.KnownTypes.IAsyncDisposableType != null)
                {
                    var hasAsyncDisposable = disposableTypes.Contains(root.KnownTypes.IAsyncDisposableType.ToDisplayString());
                    
                    codeWriter.Line($"_disposables.Add(new DisposableWrapper({(hasDisposable ? "service" : "null")}, {(hasAsyncDisposable ? "service" : "null")}));");
                }
                else if (hasDisposable)
                {
                    codeWriter.Line($"_disposables.Add(service as {typeof(IDisposable)});");
                }
            }
            valueCallback(codeWriter, w => w.Append($"service"));
        }
        else
        {
            GenerateCallSite(codeWriter, rootReference, serviceCallSite, valueCallback);
        }
    }

    private void WriteResolutionCall(CodeWriter codeWriter, ServiceIdentity other, string reference)
    {
        if (other.IsMainImplementation)
        {
            codeWriter.Append($"((IServiceProvider<{other.Type}>){reference}).GetService()");
        }
        else
        {
            codeWriter.Append($"{reference}.{GetResolutionServiceName(other)}()");
        }
    }

    private static void AppendMemberReference(CodeWriter codeWriter, ISymbol method, MemberLocation memberLocation, string rootReference)
    {
        if (method.IsStatic)
        {
            if (memberLocation == MemberLocation.Module)
            {
                codeWriter.Append($"{method.ContainingType}.");
            }
        }
        else
        {
            switch (memberLocation)
            {
                case MemberLocation.Module:
                case MemberLocation.Root:
                    codeWriter.Append($"this.");
                    break;
                case MemberLocation.Scope:
                    codeWriter.Append($"{rootReference}.");
                    break;
            }
        }

        codeWriter.Append($"{method.Name}");
    }

    private static void AppendMemberGenericParameters(CodeWriter codeWriter, ISymbol symbol)
    {
        if (symbol is IMethodSymbol method)
        {
            if (method.TypeArguments.Length > 0)
            {
                codeWriter.AppendRaw("<");
                foreach (var typeArgument in method.TypeArguments)
                {
                    codeWriter.Append($"{typeArgument}, ");
                }
                codeWriter.RemoveTrailingComma();
                codeWriter.AppendRaw(">");
            }
        }
    }

    private void AppendParameters(CodeWriter codeWriter, ServiceCallSite[] parameters, KeyValuePair<IParameterSymbol, ServiceCallSite>[] optionalParameters)
    {
        foreach (var parameter in parameters)
        {
            WriteResolutionCall(codeWriter, parameter.Identity, "this");
            codeWriter.AppendRaw(", ");
        }

        foreach (var pair in optionalParameters)
        {
            codeWriter.Append($"{pair.Key.Name}: ");
            WriteResolutionCall(codeWriter, pair.Value.Identity, "this");
            codeWriter.AppendRaw(", ");
        }
        codeWriter.RemoveTrailingComma();
    }

    private void GenerateCallSite(CodeWriter codeWriter, string rootReference, ServiceCallSite serviceCallSite, Action<CodeWriter, CodeWriterDelegate> valueCallback)
    {
        switch (serviceCallSite)
        {
            case ConstructorCallSite transientCallSite:
                valueCallback(codeWriter, w =>
                {
                    w.Append($"new {transientCallSite.ImplementationType}(");
                    AppendParameters(w, transientCallSite.Parameters, transientCallSite.OptionalParameters);
                    w.Append($")");
                });
                break;
            case MemberCallSite memberCallSite:
                valueCallback(codeWriter, w =>
                {
                    AppendMemberReference(w, memberCallSite.Member, memberCallSite.MemberLocation, rootReference);
                });
                break;
            case FactoryCallSite methodCallSite:
                valueCallback(codeWriter, w =>
                {
                    AppendMemberReference(w, methodCallSite.Member, methodCallSite.MemberLocation, rootReference);
                    AppendMemberGenericParameters(w, methodCallSite.Member);

                    w.AppendRaw("(");
                    AppendParameters(w, methodCallSite.Parameters, methodCallSite.OptionalParameters);
                    w.Append($")");
                });
                break;
            case ArrayServiceCallSite arrayServiceCallSite:
                valueCallback(codeWriter, w =>
                {
                    using (w.Scope($"new {arrayServiceCallSite.ItemType}[]", newLine: false))
                    {
                        foreach (var item in arrayServiceCallSite.Items)
                        {
                            WriteResolutionCall(codeWriter, item.Identity, "this");
                            w.LineRaw(", ");
                        }
                    }
                });
                break;
            case ServiceProviderCallSite:
                valueCallback(codeWriter, w => w.AppendRaw("this"));
                break;
            case ServiceProviderIsServiceCallSite:
            case ScopeFactoryCallSite:
                valueCallback(codeWriter, w => w.AppendRaw(rootReference));
                break;
        }
    }

    private void Execute(GeneratorContext context)
    {
        try
        {
            var roots = new ServiceProviderBuilder(context).BuildRoots();

            foreach (var root in roots)
            {
                var codeWriter = new CodeWriter();
                codeWriter.UseNamespace("Jab");
                codeWriter.UseNamespace("System");
                codeWriter.UseNamespace("System.Diagnostics");
                codeWriter.UseNamespace("System.Diagnostics.CodeAnalysis");
                codeWriter.Line($"using static Jab.JabHelpers;");
                using (root.Type.ContainingNamespace.IsGlobalNamespace ?
                           default :
                           codeWriter.Namespace($"{root.Type.ContainingNamespace.ToDisplayString()}"))
                {
                    // TODO: implement infinite nesting
                    using CodeWriter.CodeWriterScope? parentTypeScope = root.Type.ContainingType is { } containingType ?
                        codeWriter.Scope($"{SyntaxFacts.GetText(containingType.DeclaredAccessibility)} partial class {containingType.Name}") :
                        null;

                    codeWriter.LineRaw(_generatedCodeAttribute);
                    codeWriter.Append($"{SyntaxFacts.GetText(root.Type.DeclaredAccessibility)} partial class {root.Type.Name}");
                    WriteInterfaces(codeWriter, root, false);
                    using (codeWriter.Scope())
                    {
                        codeWriter.Line($"private Scope? _rootScope;");
                        WriteCacheLocations(root, codeWriter, isScope: false);

                        foreach (var rootService in root.RootCallSites)
                        {
                            var rootServiceType = rootService.Identity.Type;
                            if (rootService.Identity.IsMainImplementation)
                            {
                                codeWriter.Append($"{rootServiceType} IServiceProvider<{rootServiceType}>.GetService()");
                            }
                            else
                            {
                                codeWriter.Append($"private {rootServiceType} {GetResolutionServiceName(rootService.Identity)}()");
                            }

                            if (rootService.Lifetime == ServiceLifetime.Scoped)
                            {
                                codeWriter.Line($" => GetRootScope().GetService<{rootServiceType}>();");
                            }
                            else
                            {
                                codeWriter.Line();
                                using (codeWriter.Scope())
                                {
                                    GenerateCallSiteWithCache(codeWriter, root,
                                        "this",
                                        rootService,
                                        (w, v) => w.Line($"return {v};"));
                                }
                            }

                            codeWriter.Line();
                        }

                        WriteNamedServiceProvider(codeWriter, root);
                        WriteServiceProvider(codeWriter, root);
                        WriteDispose(codeWriter, root, isScoped: false);
                        WritePublicGetServiceMethods(codeWriter);

                        codeWriter.Line($"public Scope CreateScope() => new Scope(this);");
                        codeWriter.Line();

                        if (root.KnownTypes.IServiceScopeFactoryType != null)
                        {
                            codeWriter.Line($"{root.KnownTypes.IServiceScopeType} {root.KnownTypes.IServiceScopeFactoryType}.CreateScope() => this.CreateScope();");
                            codeWriter.Line();
                        }

                        if (root.KnownTypes.IServiceProviderIsServiceType != null)
                        {
                            using var _ = codeWriter.Scope($"bool {root.KnownTypes.IServiceProviderIsServiceType}.IsService(Type service) => ", "", "");
                            bool first = true;
                            foreach (var rootService in root.RootCallSites)
                            {
                                if (first)
                                {
                                    first = false;
                                }
                                else
                                {
                                    codeWriter.Line($" ||");
                                }
                                codeWriter.Append($"typeof({rootService.Identity.Type}) == service");
                            }
                            if (first)
                            {
                                codeWriter.Append($"false");
                            }
                            codeWriter.Line($";");
                        }

                        codeWriter.Append($"public partial class Scope");
                        WriteInterfaces(codeWriter, root, true);
                        using (codeWriter.Scope())
                        {
                            WriteCacheLocations(root, codeWriter, isScope: true);
                            codeWriter.Line($"private {root.Type} _root;");
                            codeWriter.Line();

                            using (codeWriter.Scope($"public Scope({root.Type} root)"))
                            {
                                codeWriter.Line($"_root = root;");
                            }
                            codeWriter.Line();

                            WritePublicGetServiceMethods(codeWriter);

                            foreach (var rootService in root.RootCallSites)
                            {
                                var rootServiceType = rootService.Identity.Type;

                                using (rootService.Identity.IsMainImplementation ?
                                           codeWriter.Scope($"{rootServiceType} IServiceProvider<{rootServiceType}>.GetService()") :
                                           codeWriter.Scope($"private {rootServiceType} {GetResolutionServiceName(rootService.Identity)}()"))
                                {
                                    if (rootService.Lifetime == ServiceLifetime.Singleton)
                                    {
                                        codeWriter.Append($"return ");
                                        WriteResolutionCall(codeWriter, rootService.Identity, "_root");
                                        codeWriter.Line($";");
                                    }
                                    else
                                    {
                                        GenerateCallSiteWithCache(codeWriter, root,
                                            "_root",
                                            rootService,
                                            (w, v) => w.Line($"return {v};"));
                                    }
                                }
                                codeWriter.Line();
                            }

                            WriteServiceProvider(codeWriter, root);
                            WriteNamedServiceProvider(codeWriter, root);

                            if (root.KnownTypes.IServiceScopeType != null)
                            {
                                codeWriter.Line($"{root.KnownTypes.IServiceProviderType} {root.KnownTypes.IServiceScopeType}.ServiceProvider => this;");
                                codeWriter.Line();
                            }
                            WriteDispose(codeWriter, root, isScoped: true);
                        }

                        using (codeWriter.Scope($"private Scope GetRootScope()"))
                        {
                            codeWriter.Line($"#nullable disable");
                            codeWriter.Line($"{typeof(LazyInitializer)}.EnsureInitialized(ref _rootScope , () => CreateScope());");
                            codeWriter.Line($"return _rootScope;");
                            codeWriter.Line($"#nullable enable");
                        }
                    }
                }
                context.AddSource($"{root.Type.Name}.Generated.cs", codeWriter.ToString());
            }
        }
        catch (Exception e)
        {
            context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.UnexpectedErrorDescriptor, Location.None, e.ToString().Replace(Environment.NewLine, " ")));
        }
    }

    private IEnumerable<IGrouping<ITypeSymbol, ServiceCallSite>> GroupNamedServices(ServiceProvider root)
    {
        return root.RootCallSites
            .Where(static s => s.Identity.IsMainNamedImplementation)
            .GroupBy<ServiceCallSite, ITypeSymbol>(static s => s.Identity.Type, SymbolEqualityComparer.Default);
    }
    private void WriteNamedServiceProvider(CodeWriter codeWriter, ServiceProvider root)
    {
        foreach (var serviceGroup in GroupNamedServices(root))
        {
            var groupType = serviceGroup.Key;
            using (codeWriter.Scope($"{groupType} INamedServiceProvider<{groupType}>.GetService(string name)"))
            {
                using (codeWriter.Scope($"switch (name)"))
                {
                    foreach (var callSite in serviceGroup)
                    {
                        codeWriter.Append($"case \"{callSite.Identity.Name}\": return ");
                        WriteResolutionCall(codeWriter, callSite.Identity, "this");
                        codeWriter.Line($";");
                    }

                    codeWriter.Line($"default: throw CreateServiceNotFoundException<{groupType}>(name);");
                }
            }
            codeWriter.Line();
        }
    }

    private void WriteServiceProvider(CodeWriter codeWriter, ServiceProvider root)
    {
        using (codeWriter.Scope($"{typeof(object)}? {typeof(IServiceProvider)}.GetService({typeof(Type)} type)"))
        {
            foreach (var rootRootCallSite in root.RootCallSites)
            {
                if (rootRootCallSite.Identity.IsMainImplementation)
                {
                    codeWriter.Append($"if (type == typeof({rootRootCallSite.Identity.Type})) return ");
                    WriteResolutionCall(codeWriter, rootRootCallSite.Identity, "this");
                    codeWriter.Line($";");
                }
            }

            codeWriter.Line($"return null;");
        }

        codeWriter.Line();

        WriteKeyedServiceProvider(codeWriter, root);
    }


    private void WriteKeyedServiceProvider(CodeWriter codeWriter, ServiceProvider root)
    {
        var iface = root.KnownTypes.IKeyedServiceProviderType;
        if (iface == null)
        {
            return;
        }

        using (codeWriter.Scope($"{typeof(object)}? {iface}.GetKeyedService({typeof(Type)} type, object? key)"))
        {
            foreach (var serviceGroup in GroupNamedServices(root))
            {
                var serviceType = serviceGroup.Key;
                using (codeWriter.Scope($"if (type == typeof({serviceType}))"))
                {
                    using (codeWriter.Scope($"switch (key)"))
                    {
                        foreach (var callSite in serviceGroup)
                        {
                            codeWriter.Append($"case \"{callSite.Identity.Name}\": return ");
                            WriteResolutionCall(codeWriter, callSite.Identity, "this");
                            codeWriter.Line($";");
                        }
                    }
                }
            }

            codeWriter.Line($"return null;");
        }

        codeWriter.Line();

        codeWriter.Line(
            $"{typeof(object)} {iface}.GetRequiredKeyedService({typeof(Type)} type, object? key) => (({iface})this).GetKeyedService(type, key) ?? throw CreateServiceNotFoundException(type, key?.ToString());");

        codeWriter.Line();
    }

    private void WritePublicGetServiceMethods(CodeWriter codeWriter)
    {
        codeWriter.Line($"[DebuggerHidden]");
        codeWriter.Line($"public T GetService<T>() => this is IServiceProvider<T> provider ? provider.GetService() : throw CreateServiceNotFoundException<T>();");
        codeWriter.Line();

        codeWriter.Line($"[DebuggerHidden]");
        codeWriter.Line($"public T GetService<T>(string name) => this is INamedServiceProvider<T> provider ? provider.GetService(name) : throw CreateServiceNotFoundException<T>(name);");
        codeWriter.Line();
    }

    private void WriteDispose(CodeWriter codeWriter, ServiceProvider root, bool isScoped)
    {
        var disposableListName = $"{typeof(List<IDisposable>)}";
        if (root.KnownTypes.IAsyncDisposableType != null)
        {
            disposableListName = $"{typeof(List<>).Namespace}.List<DisposableWrapper>";
            if (!isScoped)
            {
                using (codeWriter.Scope($"private struct DisposableWrapper"))
                {
                    codeWriter.Line($"private readonly {typeof(IDisposable)}? _disposable;");
                    codeWriter.Line($"private readonly {root.KnownTypes.IAsyncDisposableType}? _asyncDisposable;");

                    codeWriter.Line();

                    using (codeWriter.Scope($"public DisposableWrapper({typeof(IDisposable)}? disposable, {root.KnownTypes.IAsyncDisposableType}? asyncDisposable)"))
                    {
                        codeWriter.Line($"_disposable = disposable;");
                        codeWriter.Line($"_asyncDisposable = asyncDisposable;");
                    }

                    codeWriter.Line();

                    using (codeWriter.Scope($"public void Dispose()"))
                    {
                        codeWriter.Line($"_disposable?.Dispose();");
                    }

                    using (codeWriter.Scope($"public async ValueTask DisposeAsync()"))
                    {
                        codeWriter.Line($"if (_asyncDisposable is not null) await _asyncDisposable.DisposeAsync();");
                    }
                }
            }
        }

        codeWriter.Line($"private {disposableListName} _disposables = new();");
        codeWriter.Line();

        if (root.KnownTypes.IAsyncDisposableType != null)

            using (codeWriter.Scope($"public void Dispose()"))
            {
                foreach (var rootService in root.RootCallSites)
                {
                    if (rootService.IsDisposable == false ||
                        (rootService.Lifetime == ServiceLifetime.Singleton && isScoped) ||
                        (rootService.Lifetime == ServiceLifetime.Scoped && !isScoped) ||
                        rootService.Lifetime == ServiceLifetime.Transient ||
                        !rootService.Identity.Type.Interfaces.Any(x => x.ToDisplayString() == typeof(IDisposable).FullName)) continue;

                    codeWriter.Line($"{GetCacheLocation(rootService.Identity)}?.Dispose();");
                }

                if (!isScoped)
                {
                    codeWriter.Line($"_rootScope?.Dispose();");
                }
                
                using (codeWriter.Scope($"foreach (var service in _disposables)"))
                {
                    codeWriter.Line($"service.Dispose();");
                }
            }

        codeWriter.Line();

        if (root.KnownTypes.IAsyncDisposableType != null)
        {
            using (codeWriter.Scope($"public async {typeof(ValueTask)} DisposeAsync()"))
            {
                foreach (var rootService in root.RootCallSites)
                {
                    if (rootService.IsDisposable == false ||
                        (rootService.Lifetime == ServiceLifetime.Singleton && isScoped) ||
                        (rootService.Lifetime == ServiceLifetime.Scoped && !isScoped) ||
                        rootService.Lifetime == ServiceLifetime.Transient) continue;

                    if (rootService.Identity.Type.Interfaces.Any(x => x.ToDisplayString() == root.KnownTypes.IAsyncDisposableType.ToDisplayString()))
                    {
                        codeWriter.Line($"if ({GetCacheLocation(rootService.Identity)} is not null) await {GetCacheLocation(rootService.Identity)}.DisposeAsync();");
                    } else if (rootService.Identity.Type.Interfaces.Any(x => x.ToDisplayString() == typeof(IDisposable).FullName))
                    {
                        codeWriter.Line($"{GetCacheLocation(rootService.Identity)}?.Dispose();");
                    }
                    
                }

                if (!isScoped)
                {
                    codeWriter.Line($"if (_rootScope is not null) await _rootScope.DisposeAsync();");
                }
                
                using (codeWriter.Scope($"foreach (var service in _disposables)"))
                {
                    codeWriter.Line($"await service.DisposeAsync();");
                }
            }
        }


        codeWriter.Line();
    }

    private static void WriteInterfaces(CodeWriter codeWriter, ServiceProvider root, bool isScope)
    {
        codeWriter.Line($" : {typeof(IDisposable)},");

        if (root.KnownTypes.IAsyncDisposableType != null)
        {
            codeWriter.Line($"   {root.KnownTypes.IAsyncDisposableType},");
        }

        codeWriter.Line($"   {typeof(IServiceProvider)},");

        if (root.KnownTypes.IKeyedServiceProviderType != null)
        {
            codeWriter.Line($"   {root.KnownTypes.IKeyedServiceProviderType},");
        }

        if (!isScope && root.KnownTypes.IServiceScopeFactoryType != null)
        {
            codeWriter.Line($"   {root.KnownTypes.IServiceScopeFactoryType},");
        }

        if (!isScope && root.KnownTypes.IServiceProviderIsServiceType != null)
        {
            codeWriter.Line($"   {root.KnownTypes.IServiceProviderIsServiceType},");
        }

        if (isScope && root.KnownTypes.IServiceScopeType != null)
        {
            codeWriter.Line($"   {root.KnownTypes.IServiceScopeType},");
        }

        HashSet<ITypeSymbol> seenServices = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        HashSet<ITypeSymbol> seenNamedServices = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var serviceCallSite in root.RootCallSites)
        {
            if (serviceCallSite.Identity.Name == null)
            {
                if (seenServices.Add(serviceCallSite.Identity.Type))
                {
                    codeWriter.Line($"   IServiceProvider<{serviceCallSite.Identity.Type}>,");
                }
            }
            else
            {
                if (seenNamedServices.Add(serviceCallSite.Identity.Type))
                {
                    codeWriter.Line($"   INamedServiceProvider<{serviceCallSite.Identity.Type}>,");
                }
            }
        }

        codeWriter.RemoveTrailingComma();
        codeWriter.Line();
    }

    private void WriteCacheLocations(ServiceProvider root, CodeWriter codeWriter, bool isScope)
    {
        foreach (var rootService in root.RootCallSites)
        {
            if ((rootService.Lifetime == ServiceLifetime.Singleton && isScope) ||
                (rootService.Lifetime == ServiceLifetime.Scoped && !isScope) ||
                rootService.Lifetime == ServiceLifetime.Transient) continue;

            codeWriter.Line($"private {rootService.ImplementationType}? {GetCacheLocation(rootService.Identity)};");
        }
        codeWriter.Line();
    }

    private string GetResolutionServiceName(ServiceIdentity identity)
    {
        if (!identity.IsMainImplementation)
        {
            return $"Get{GetServiceExpandedName(identity)}";
        }

        throw new InvalidOperationException("Main implementation should be resolved via GetService<T> call");
    }

    private string GetCacheLocation(ServiceIdentity identity)
    {
        return $"_{GetServiceExpandedName(identity)}";
    }

    private string GetServiceExpandedName(ServiceIdentity identity)
    {
        StringBuilder builder = new();

        void Traverse(ITypeSymbol symbol)
        {
            builder.Append(symbol.Name);
            if (symbol is INamedTypeSymbol { IsGenericType: true } genericType)
            {
                builder.Append("_");
                foreach (var typeArgument in genericType.TypeArguments)
                {
                    Traverse(typeArgument);
                }
            }
        }

        Traverse(identity.Type);

        if (identity.Name != null)
        {
            builder.Append("_");
            builder.Append(identity.Name);
        }

        if (identity.ReverseIndex != null)
        {
            builder.Append("_");
            builder.Append(identity.ReverseIndex);
        }
        return builder.ToString();
    }

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.RegisterCompilationStartAction(compilationStartAnalysisContext =>
        {
            var syntaxCollector = new SyntaxCollector();
            compilationStartAnalysisContext.RegisterSyntaxNodeAction(analysisContext =>
            {
                syntaxCollector.OnVisitSyntaxNode(analysisContext.Node);
            }, SyntaxKind.ClassDeclaration, SyntaxKind.InterfaceDeclaration, SyntaxKind.InvocationExpression);

            compilationStartAnalysisContext.RegisterCompilationEndAction(compilationContext =>
            {
                Execute(new GeneratorContext(compilationContext, syntaxCollector));
            });
        });
    }

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = new[]
    {
        DiagnosticDescriptors.UnexpectedErrorDescriptor,
        DiagnosticDescriptors.ServiceRequiredToConstructNotRegistered,
        DiagnosticDescriptors.MemberReferencedByInstanceOrFactoryAttributeNotFound,
        DiagnosticDescriptors.MemberReferencedByInstanceOrFactoryAttributeAmbiguous,
        DiagnosticDescriptors.ServiceProviderTypeHasToBePartial,
        DiagnosticDescriptors.ImportedTypeNotMarkedWithModuleAttribute,
        DiagnosticDescriptors.ImplementationTypeRequiresPublicConstructor,
        DiagnosticDescriptors.CyclicDependencyDetected,
        DiagnosticDescriptors.MissingServiceProviderAttribute,
        DiagnosticDescriptors.NoServiceTypeRegistered,
        DiagnosticDescriptors.ImplementationTypeAndFactoryNotAllowed,
        DiagnosticDescriptors.FactoryMemberMustBeAMethodOrHaveDelegateType,
        DiagnosticDescriptors.ServiceNameMustBeAlphanumeric,
        DiagnosticDescriptors.ImplicitIEnumerableNotNamed,
        DiagnosticDescriptors.BuiltInServicesAreNotNamed,
        DiagnosticDescriptors.NoServiceTypeAndNameRegistered,
        DiagnosticDescriptors.NamedServiceRequiredToConstructNotRegistered,
        DiagnosticDescriptors.OnlyStringKeysAreSupported,
        DiagnosticDescriptors.NullableServiceNotRegistered,
        DiagnosticDescriptors.NullableServiceRegistered,
    }.ToImmutableArray();

    private static string ReadAttributesFile()
    {
        using var manifestResourceStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Jab.Attributes.cs");
        Debug.Assert(manifestResourceStream != null);
        using var reader = new StreamReader(manifestResourceStream);
        return reader.ReadToEnd();
    }
}