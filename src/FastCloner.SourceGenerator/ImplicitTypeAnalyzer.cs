using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace FastCloner.SourceGenerator
{
    internal static class ImplicitTypeAnalyzer
    {
        public static bool TryAnalyze(
            ITypeSymbol type,
            Compilation compilation,
            bool nullabilityEnabled,
            TargetFramework targetFramework,
            Dictionary<ITypeSymbol, TypeModel?> cache,
            HashSet<ITypeSymbol> processingStack,
            out TypeModel? implicitModel)
        {
            implicitModel = null;

            if (processingStack.Contains(type))
            {
                return true;
            }

            if (cache.TryGetValue(type, out var cached))
            {
                implicitModel = cached;
                return cached != null;
            }

            var hasInterface = TypeAnalyzer.HasClonableInterface(type);
            
            if (!TypeAnalyzer.IsImplicitCandidate(type) && !hasInterface)
            {
                return false;
            }

            if (type is not INamedTypeSymbol namedType)
            {
                return false;
            }

            processingStack.Add(type);

            var memberAnalyses = MemberCollector.GetMembers(namedType, compilation, nullabilityEnabled);
            List<MemberModel> finalImplicitMembers = [ ];
            List<TypeModel> childRelatedTypes = [ ];
            var implicitNestedMembers = new Dictionary<string, MemberModel>();

            var success = true;
            var hasUnsafeReferenceMember = false;

            foreach (var analysis in memberAnalyses)
            {
                var m = analysis.Model;

                if (!m.IsValueType && !TypeAnalyzer.IsSafeType(analysis.Type, compilation))
                {
                    hasUnsafeReferenceMember = true;
                }

                if (m.TypeKind is MemberTypeKind.Other or MemberTypeKind.Implicit)
                {
                    if (TryAnalyze(analysis.Type, compilation, nullabilityEnabled, targetFramework, cache, processingStack, out var childModel))
                    {
                        m = m with { TypeKind = MemberTypeKind.Implicit, RequiresFastCloner = false };
                        if (childModel != null)
                        {
                            childRelatedTypes.Add(childModel);
                        }
                    }
                    else
                    {
                        success = false;
                        break;
                    }
                }
                else if (m.RequiresFastCloner)
                {
                    var handled = false;

                    bool TryHandleComponent(ITypeSymbol componentType, out MemberModel? componentMember)
                    {
                        componentMember = null;
                        if (TryAnalyze(componentType, compilation, nullabilityEnabled, targetFramework, cache, processingStack, out var compModel))
                        {
                            if (compModel != null)
                            {
                                childRelatedTypes.Add(compModel);
                            }

                            var typeName = componentType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                            componentMember = new MemberModel("Implicit_" + componentType.Name,
                                                              typeName,
                                                              false,
                                                              false,
                                                              false,
                                                              MemberTypeKind.Implicit,
                                                              null,
                                                              null,
                                                              null,
                                                              false,
                                                              false,
                                                              false,
                                                              false,
                                                              false,
                                                              false,
                                                              false,
                                                              false,
                                                              CollectionKind.None,
                                                              null,
                                                              componentType.IsValueType,
                                                              false,
                                                              false,
                                                              0,
                                                              false,
                                                              true,
                                                              true,
                                                              true,
                                                              MemberCloneBehavior.Clone);
                            return true;
                        }

                        return false;
                    }

                    if (m.TypeKind is MemberTypeKind.Array or MemberTypeKind.Collection)
                    {
                        var elemType = m.TypeKind == MemberTypeKind.Array ? ((IArrayTypeSymbol)analysis.Type).ElementType : TypeAnalyzer.GetCollectionElementType(analysis.Type, compilation);

                        if (elemType != null && TryHandleComponent(elemType, out var elemMember))
                        {
                            m = m with { RequiresFastCloner = false };
                            if (elemMember != null)
                            {
                                implicitNestedMembers[elemMember.Value.TypeFullName] = elemMember.Value;
                            }

                            handled = true;
                        }
                    }
                    else if (m.TypeKind == MemberTypeKind.Dictionary)
                    {
                        var dictTypes = TypeAnalyzer.GetDictionaryTypes(analysis.Type, compilation);
                        if (dictTypes.HasValue)
                        {
                            var keyOk = m.KeyIsSafe || m.KeyIsClonable;
                            var valOk = m.ValueIsSafe || m.ValueIsClonable;

                            if (!keyOk)
                            {
                                if (TryHandleComponent(dictTypes.Value.KeyType, out var keyMember))
                                {
                                    if (keyMember != null)
                                    {
                                        implicitNestedMembers[keyMember.Value.TypeFullName] = keyMember.Value;
                                    }

                                    keyOk = true;
                                }
                            }

                            if (!valOk)
                            {
                                if (TryHandleComponent(dictTypes.Value.ValueType, out var valMember))
                                {
                                    if (valMember != null)
                                    {
                                        implicitNestedMembers[valMember.Value.TypeFullName] = valMember.Value;
                                    }

                                    valOk = true;
                                }
                            }

                            if (keyOk && valOk)
                            {
                                m = m with { RequiresFastCloner = false };
                                handled = true;
                            }
                        }
                    }

                    if (!handled)
                    {
                        success = false;
                        break;
                    }
                }

                finalImplicitMembers.Add(m);
            }

            processingStack.Remove(type);

            if (success)
            {
                var relatedTypesMap = new Dictionary<string, TypeModel>();
                foreach (var child in childRelatedTypes)
                {
                    relatedTypesMap[child.FullyQualifiedName] = child;
                    foreach (var rel in child.RelatedTypes)
                    {
                        relatedTypesMap[rel.FullyQualifiedName] = rel;
                    }
                }

                var flags = TypeAnalyzer.GetStructureFlags(namedType);
                var canHaveCircularRefs = hasUnsafeReferenceMember || flags.HasClonableBaseClass;
                var hasParameterlessConstructor = TypeAnalyzer.HasParameterlessConstructor(namedType);
                var trustNullability = namedType.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "FastCloner.SourceGenerator.Shared.FastClonerTrustNullabilityAttribute");

                implicitModel = new TypeModel(TypeAnalyzer.GetNamespace(namedType),
                                              namedType.Name,
                                              namedType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                                              TypeAnalyzer.GetAccessibilityString(namedType.DeclaredAccessibility),
                                              flags.IsStruct,
                                              flags.IsSealed,
                                              namedType.IsAbstract,
                                              namedType.IsRecord,
                                              flags.HasClonableBaseClass,
                                              hasInterface,
                                              canHaveCircularRefs,
                                              canHaveCircularRefs,
                                              false,
                                              new EquatableArray<MemberModel>(finalImplicitMembers.ToArray()),
                                              new EquatableArray<string>(TypeAnalyzer.GetTypeParameters(namedType).ToArray()),
                                              new EquatableArray<string>(TypeAnalyzer.GetTypeConstraints(namedType).ToArray()),
                                              new EquatableArray<TypeModel>(relatedTypesMap.Values.ToArray()),
                                              new EquatableArray<MemberModel>(implicitNestedMembers.Values.ToArray()),
                                              EquatableArray<TypeModel>.Empty,
                                              nullabilityEnabled,
                                              trustNullability,
                                              null,
                                              false,
                                              hasParameterlessConstructor,
                                              compilation.GetTypeByMetadataName("System.Diagnostics.CodeAnalysis.NotNullIfNotNullAttribute") != null,
                                              targetFramework);

                cache[type] = implicitModel;
                return true;
            }

            return false;
        }
    }
}