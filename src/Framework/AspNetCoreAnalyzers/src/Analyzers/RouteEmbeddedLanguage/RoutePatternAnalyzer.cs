// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Microsoft.AspNetCore.Analyzers.RouteEmbeddedLanguage.Infrastructure;
using Microsoft.AspNetCore.Analyzers.RouteEmbeddedLanguage.Infrastructure.VirtualChars;
using Microsoft.AspNetCore.Analyzers.RouteEmbeddedLanguage.RoutePattern;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Elfie.Model.Structures;

namespace Microsoft.AspNetCore.Analyzers.RouteEmbeddedLanguage;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class RoutePatternAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(new[]
    {
        DiagnosticDescriptors.RoutePatternIssue,
        DiagnosticDescriptors.RoutePatternUnusedParameter,
        DiagnosticDescriptors.RoutePatternAddParameterConstraint
    });

    public void Analyze(SemanticModelAnalysisContext context)
    {
        var semanticModel = context.SemanticModel;
        var syntaxTree = semanticModel.SyntaxTree;
        var cancellationToken = context.CancellationToken;

        var root = syntaxTree.GetRoot(cancellationToken);
        WellKnownTypes? wellKnownTypes = null;
        Analyze(context, root, ref wellKnownTypes, cancellationToken);
    }

    private void Analyze(
        SemanticModelAnalysisContext context,
        SyntaxNode node,
        ref WellKnownTypes? wellKnownTypes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var child in node.ChildNodesAndTokens())
        {
            if (child.IsNode)
            {
                Analyze(context, child.AsNode()!, ref wellKnownTypes, cancellationToken);
            }
            else
            {
                var token = child.AsToken();
                if (!RouteStringSyntaxDetector.IsRouteStringSyntaxToken(token, context.SemanticModel, cancellationToken))
                {
                    continue;
                }

                if (wellKnownTypes == null && !WellKnownTypes.TryGetOrCreate(context.SemanticModel.Compilation, out wellKnownTypes))
                {
                    return;
                }

                var usageContext = RoutePatternUsageDetector.BuildContext(token, context.SemanticModel, wellKnownTypes, cancellationToken);

                var virtualChars = CSharpVirtualCharService.Instance.TryConvertToVirtualChars(token);
                var tree = RoutePatternParser.TryParse(virtualChars, supportTokenReplacement: usageContext.IsMvcAttribute);
                if (tree == null)
                {
                    continue;
                }

                // Add warnings from the route.
                foreach (var diag in tree.Diagnostics)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.RoutePatternIssue,
                        Location.Create(context.SemanticModel.SyntaxTree, diag.Span),
                        DiagnosticDescriptors.RoutePatternIssue.DefaultSeverity,
                        additionalLocations: null,
                        properties: null,
                        diag.Message));
                }

                // The route has an associated method, e.g. it's a route in an attribute on an MVC action or is in a Map method.
                // Analyze the route and method together to detect issues.
                if (usageContext.MethodSymbol != null)
                {
                    var routeParameterNames = new HashSet<string>(tree.RouteParameters.Keys, StringComparer.OrdinalIgnoreCase);
                    foreach (var parameter in usageContext.MethodSymbol.Parameters)
                    {
                        var parameterName = parameter.Name;

                        if (routeParameterNames.Remove(parameterName))
                        {
                            var routeParameter = tree.RouteParameters[parameterName];
                            if (HasTypePolicy(routeParameter.Policies))
                            {
                                continue;
                            }

                            var policy = CalculatePolicyFromType(parameter.Type, wellKnownTypes);
                            if (policy == null)
                            {
                                continue;
                            }

                            var parameterList = usageContext.MethodSyntax switch
                            {
                                BaseMethodDeclarationSyntax methodSyntax => methodSyntax.ParameterList,
                                ParenthesizedLambdaExpressionSyntax lambdaExpressionSyntax => lambdaExpressionSyntax.ParameterList,
                                _ => throw new InvalidOperationException($"Unexpected method syntax: {usageContext.MethodSyntax.GetType().FullName}")
                            };

                            ParameterSyntax? parameterSyntax = null;
                            foreach (var item in parameterList.Parameters)
                            {
                                if (string.Equals(item.Identifier.Text, parameterName, StringComparison.OrdinalIgnoreCase))
                                {
                                    parameterSyntax = item;
                                    break;
                                }
                            }

                            Debug.Assert(parameterSyntax != null, $"Couldn't find {parameterName} in method syntax.");

                            var properties = new Dictionary<string, string>
                            {
                                ["RouteParameterName"] = parameterName,
                                ["RouteParameterPolicy"] = policy
                            };

                            context.ReportDiagnostic(Diagnostic.Create(
                                DiagnosticDescriptors.RoutePatternAddParameterConstraint,
                                Location.Create(context.SemanticModel.SyntaxTree, parameterSyntax.Span),
                                DiagnosticDescriptors.RoutePatternAddParameterConstraint.DefaultSeverity,
                                additionalLocations: new List<Location> { Location.Create(context.SemanticModel.SyntaxTree, routeParameter.ParameterNode.GetSpan()) },
                                properties: properties.ToImmutableDictionary(),
                                parameterName));
                        }
                    }

                    foreach (var unusedParameterName in routeParameterNames)
                    {
                        var unusedParameter = tree.RouteParameters[unusedParameterName];
                        var properties = new Dictionary<string, string>
                        {
                            ["RouteParameterName"] = unusedParameter.Name,
                            ["RouteParameterPolicy"] = string.Join(string.Empty, unusedParameter.Policies),
                            ["RouteParameterIsOptional"] = unusedParameter.IsOptional.ToString()
                        };

                        context.ReportDiagnostic(Diagnostic.Create(
                            DiagnosticDescriptors.RoutePatternUnusedParameter,
                            Location.Create(context.SemanticModel.SyntaxTree, unusedParameter.ParameterNode.GetSpan()),
                            DiagnosticDescriptors.RoutePatternUnusedParameter.DefaultSeverity,
                            additionalLocations: null,
                            properties: properties.ToImmutableDictionary(),
                            unusedParameterName));
                    }
                }
            }
        }
    }

    private static string? CalculatePolicyFromType(ITypeSymbol type, WellKnownTypes wellKnownTypes)
    {
        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean:
                return "bool";
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
                return "int";
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
                return "long";
            case SpecialType.System_Decimal:
                return "decimal";
            case SpecialType.System_Single:
                return "float";
            case SpecialType.System_Double:
                return "double";
            case SpecialType.System_Nullable_T:
                break;
            case SpecialType.System_DateTime:
                return "datetime";
            default:
                if (IsNullable(type, out var underlyingType))
                {
                    return CalculatePolicyFromType(underlyingType, wellKnownTypes);
                }
                if (SymbolEqualityComparer.Default.Equals(type, wellKnownTypes.Guid))
                {
                    return "guid";
                }
                break;
        }

        return null;
    }

    public static bool IsNullable(ITypeSymbol symbol, [NotNullWhen(true)] out ITypeSymbol? underlyingType)
    {
        if (symbol.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            underlyingType = ((INamedTypeSymbol)symbol).TypeArguments[0];
            return true;
        }

        underlyingType = null;
        return false;
    }

    private static bool HasTypePolicy(IImmutableList<string> routeParameterPolicy)
    {
        foreach (var policy in routeParameterPolicy)
        {
            var isTypePolicy = policy.TrimStart(':') switch
            {
                "int" => true,
                "long" => true,
                "bool" => true,
                "datetime" => true,
                "decimal" => true,
                "double" => true,
                "float" => true,
                "guid" => true,
                _ => false
            };

            if (isTypePolicy)
            {
                return true;
            }
        }

        return false;
    }

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterSemanticModelAction(Analyze);
    }
}
