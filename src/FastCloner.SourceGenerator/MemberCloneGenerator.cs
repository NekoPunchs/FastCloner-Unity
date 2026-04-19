using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace FastCloner.SourceGenerator
{
    internal static class MemberCloneGenerator
    {
        public static string GetMemberAssignment(CloneGeneratorContext context, MemberModel member, string sourceVar, string stateVar, string indent = "            ")
        {
            var memberName = member.Name;
            var nf = !member.IsNullable && !member.IsValueType ? "!" : "";

            switch (member)
            {
                case { IsProperty: false, IsReadOnly: true }:
                case { IsProperty: true, HasGetter: true, HasSetter: false, IsInitOnly: false }:
                    return string.Empty;
                default:
                    if (member.IsShallowClone)
                    {
                        return $"{memberName} = {sourceVar}.{memberName}";
                    }

                    switch (member.TypeKind)
                    {
                        case MemberTypeKind.Safe:
                        {
                            return $"{memberName} = {sourceVar}.{memberName}";
                        }
                        case MemberTypeKind.Clonable:
                        {
                            var extensionClassName = GetExtensionClassName(member);
                            return $"{memberName} = {extensionClassName}.InternalFastDeepClone({sourceVar}.{memberName}, {stateVar}){nf}";
                        }
                        case MemberTypeKind.Implicit:
                        {
                            if (context.ShouldInline(member.TypeFullName) &&
                                context.TryGetImplicitTypeModel(member.TypeFullName, out var implicitModel))
                            {
                                var inlineModelDefault = implicitModel.NeedsStateTracking;
                                var inlineMemberNeedsState = context.NeedsCircularState(member.TypeFullName, inlineModelDefault) && context.NeedsStateTracking;
                                var inlineShouldPassState = inlineMemberNeedsState || stateVar != "null";
                                var inlineActualStateVar = inlineShouldPassState ? stateVar : "null";

                                return $"{memberName} = {GetImplicitCloneExpression(context, implicitModel, $"{sourceVar}.{memberName}", inlineActualStateVar, indent, member.IsNullable)}{nf}";
                            }

                            var helperMethodName = context.GetOrCreateHelperMethodName(member);
                            context.TryGetImplicitTypeModel(member.TypeFullName, out implicitModel);

                            var modelDefault = implicitModel?.NeedsStateTracking ?? false;
                            var memberNeedsState = context.NeedsCircularState(member.TypeFullName, modelDefault) && context.NeedsStateTracking;
                            var isRegisteredType = helperMethodName == "Clone";
                            var shouldPassState = memberNeedsState || (isRegisteredType && stateVar != "null");
                            var actualStateVar = shouldPassState ? stateVar : "null";

                            return $"{memberName} = {GetHelperMethodCall(context, helperMethodName, $"{sourceVar}.{memberName}", shouldPassState, actualStateVar)}{nf}";
                        }
                        case MemberTypeKind.Collection:
                        case MemberTypeKind.Dictionary:
                        case MemberTypeKind.Array:
                        case MemberTypeKind.MultiDimArray:
                        {
                            var helperMethodName = context.GetOrCreateHelperMethodName(member);
                            var memberNeedsState = MemberNeedsCircularRefTracking(context, member);
                            var actualStateVar = memberNeedsState ? stateVar : "null";

                            return $"{memberName} = {GetHelperMethodCall(context, helperMethodName, $"{sourceVar}.{memberName}", memberNeedsState, actualStateVar)}{nf}";
                        }
                        case MemberTypeKind.Object:
                        case MemberTypeKind.Other:
                        default:
                            context.NeedsClonerClass = true;
                            return $"{memberName} = Cloner<{member.TypeFullName}>.Clone({sourceVar}.{memberName}, {stateVar}){nf}";
                    }
            }
        }

        public static void WriteMemberCloning(CloneGeneratorContext context, MemberModel member, string resultVar, string sourceVar, string stateVar)
        {
            var memberName = member.Name;
            var nf = !member.IsNullable && !member.IsValueType ? "!" : "";
            var sb = context.Source;

            switch (member)
            {
                case { IsProperty: false, IsReadOnly: true }:
                case { IsProperty: true, IsInitOnly: true }:
                    return;
                case { IsProperty: true, HasGetter: true, HasSetter: false, IsInitOnly: false }:
                    WriteGetterOnlyCollectionPopulation(context, member, resultVar, sourceVar, stateVar);
                    return;
                default:
                    if (member.IsShallowClone)
                    {
                        sb.AppendLine($"            {resultVar}.{memberName} = {sourceVar}.{memberName};");
                        return;
                    }

                    switch (member.TypeKind)
                    {
                        case MemberTypeKind.Safe:
                            sb.AppendLine($"            {resultVar}.{memberName} = {sourceVar}.{memberName};");
                        break;

                        case MemberTypeKind.Clonable:
                        {
                            if (member.ElementHasClonableInterface)
                            {
                                sb.AppendLine($"            {resultVar}.{memberName} = {sourceVar}.{memberName}.Clone(true);");
                                break;
                            }

                            var extensionClassName = GetExtensionClassName(member);
                            sb.AppendLine($"            {resultVar}.{memberName} = {extensionClassName}.InternalFastDeepClone({sourceVar}.{memberName}, {stateVar}){nf};");
                            break;
                        }

                        case MemberTypeKind.Implicit:
                        {
                            if (context.ShouldInline(member.TypeFullName) &&
                                context.TryGetImplicitTypeModel(member.TypeFullName, out var implicitModel))
                            {
                                var inlineModelDefault = implicitModel.NeedsStateTracking;
                                var inlineMemberNeedsState = context.NeedsCircularState(member.TypeFullName, inlineModelDefault) && context.NeedsStateTracking;
                                var inlineShouldPassState = inlineMemberNeedsState || stateVar != "null";
                                var inlineActualStateVar = inlineShouldPassState ? stateVar : "null";

                                sb.AppendLine(GetImplicitCloneStatement(context, implicitModel, member.Name, resultVar, sourceVar, inlineActualStateVar, member.IsNullable));
                                break;
                            }

                            var helperMethodName = context.GetOrCreateHelperMethodName(member);
                            context.TryGetImplicitTypeModel(member.TypeFullName, out implicitModel);
                            var modelDefault = implicitModel?.NeedsStateTracking ?? false;
                            var memberNeedsState = context.NeedsCircularState(member.TypeFullName, modelDefault) && context.NeedsStateTracking;
                            var isRegisteredType = helperMethodName == "Clone";
                            var shouldPassState = memberNeedsState || (isRegisteredType && stateVar != "null");
                            var actualStateVar = shouldPassState ? stateVar : "null";

                            sb.AppendLine($"            {resultVar}.{memberName} = {GetHelperMethodCall(context, helperMethodName, $"{sourceVar}.{memberName}", shouldPassState, actualStateVar)}{nf};");
                        }
                        break;

                        case MemberTypeKind.Collection:
                        case MemberTypeKind.Dictionary:
                        case MemberTypeKind.Array:
                        case MemberTypeKind.MultiDimArray:
                        {
                            var helperMethodName = context.GetOrCreateHelperMethodName(member);
                            var memberNeedsState = MemberNeedsCircularRefTracking(context, member);
                            var actualStateVar = memberNeedsState ? stateVar : "null";
                            sb.AppendLine($"            {resultVar}.{memberName} = {GetHelperMethodCall(context, helperMethodName, $"{sourceVar}.{memberName}", memberNeedsState, actualStateVar)}{nf};");
                        }
                        break;

                        case MemberTypeKind.Object:
                        case MemberTypeKind.Other:
                        default:
                            context.NeedsClonerClass = true;
                            sb.AppendLine($"            {resultVar}.{memberName} = Cloner<{member.TypeFullName}>.Clone({sourceVar}.{memberName}, {stateVar}){nf};");
                        break;
                    }

                break;
            }
        }

        private static void WriteGetterOnlyCollectionPopulation(CloneGeneratorContext context, MemberModel member, string resultVar, string sourceVar, string stateVar)
        {
            var sb = context.Source;
            var memberName = member.Name;

            if (member.TypeKind != MemberTypeKind.Collection && member.TypeKind != MemberTypeKind.Dictionary)
            {
                return;
            }

            var targetVar = $"target_{context.GetNextVariableId()}";
            sb.AppendLine($"            var {targetVar} = {resultVar}.{memberName};");
            sb.AppendLine($"            if ({sourceVar}.{memberName} != null && {targetVar} != null)");
            sb.AppendLine("            {");
            sb.AppendLine($"                {targetVar}.Clear();");

            if (member.IsShallowClone)
            {
                if (member.TypeKind == MemberTypeKind.Dictionary)
                {
                    sb.AppendLine($"                foreach (var kvp in {sourceVar}.{memberName})");
                    sb.AppendLine("                {");
                    sb.AppendLine($"                    {targetVar}[kvp.Key!] = kvp.Value!;");
                    sb.AppendLine("                }");
                }
                else
                {
                    var addMethod = GetAddMethodForCollection(member.CollectionKind);
                    sb.AppendLine($"                foreach (var item in {sourceVar}.{memberName})");
                    sb.AppendLine("                {");
                    sb.AppendLine($"                    {targetVar}.{addMethod}(item!);");
                    sb.AppendLine("                }");
                }
            }
            else
            {
                var helperMethodName = context.GetOrCreateHelperMethodName(member);
                var memberNeedsState = MemberNeedsCircularRefTracking(context, member);
                var actualStateVar = memberNeedsState ? stateVar : "null";
                var clonedVar = $"cloned_{context.GetNextVariableId()}";
                var helperCall = GetHelperMethodCall(context, helperMethodName, $"{sourceVar}.{memberName}", memberNeedsState, actualStateVar);
                sb.AppendLine($"                var {clonedVar} = {helperCall};");
                sb.AppendLine($"                if ({clonedVar} != null)");
                sb.AppendLine("                {");

                if (member.TypeKind == MemberTypeKind.Dictionary)
                {
                    sb.AppendLine($"                    foreach (var kvp in {clonedVar})");
                    sb.AppendLine("                    {");
                    sb.AppendLine($"                        {targetVar}[kvp.Key!] = kvp.Value!;");
                    sb.AppendLine("                    }");
                }
                else
                {
                    var addMethod = GetAddMethodForCollection(member.CollectionKind);
                    sb.AppendLine($"                    foreach (var item in {clonedVar})");
                    sb.AppendLine("                    {");
                    sb.AppendLine($"                        {targetVar}.{addMethod}(item!);");
                    sb.AppendLine("                    }");
                }

                sb.AppendLine("                }");
            }

            sb.AppendLine("            }");
        }

        private static string GetAddMethodForCollection(CollectionKind kind) =>
            kind switch
            {
                CollectionKind.Queue or CollectionKind.ConcurrentQueue => "Enqueue",
                CollectionKind.Stack or CollectionKind.ConcurrentStack => "Push",
                CollectionKind.LinkedList                              => "AddLast",
                _                                                      => "Add"
            };

        public static bool MemberNeedsCircularRefTracking(CloneGeneratorContext context, MemberModel member)
        {
            if (member.PreserveIdentity is { })
            {
                return member.PreserveIdentity.Value;
            }

            if (!context.NeedsStateTracking)
            {
                return false;
            }

            switch (member.TypeKind)
            {
                case MemberTypeKind.Safe:
                    return false;
                case MemberTypeKind.Clonable:
                    return true;
                case MemberTypeKind.Collection:
                case MemberTypeKind.Array:
                case MemberTypeKind.MultiDimArray:
                {
                    if (member.ElementIsSafe)
                    {
                        return false;
                    }

                    if (member.ElementHasClonableAttr)
                    {
                        return true;
                    }

                    break;
                }
            }

            return true;
        }

        private static string GetImplicitCloneStatement(CloneGeneratorContext context,
                                                        TypeModel implicitModel,
                                                        string memberName,
                                                        string resultVar,
                                                        string sourceVar,
                                                        string stateVar,
                                                        bool isMemberNullable)
        {
            var sb = new StringBuilder();
            var sourceProp = $"{sourceVar}.{memberName}";
            var safeName = $"l_{memberName}_{context.GetNextVariableId()}";

            sb.AppendLine($"            var {safeName} = {sourceProp};");

            var skipNullCheck = context.Model.TrustNullability && !isMemberNullable;

            if (!implicitModel.IsStruct && !skipNullCheck)
            {
                sb.AppendLine($"            if ({safeName} != null)");
                sb.AppendLine("            {");
            }

            var typeName = implicitModel.FullyQualifiedName;
            var assignmentIndent = !implicitModel.IsStruct && !skipNullCheck ? "                    " : "                ";

            if (!implicitModel.IsStruct && !skipNullCheck)
            {
                sb.AppendLine($"                {resultVar}.{memberName} = new {typeName}");
                sb.AppendLine("                {");
            }
            else
            {
                sb.AppendLine($"            {resultVar}.{memberName} = new {typeName}");
                sb.AppendLine("            {");
            }

            List<string> assignments = [ ];

            foreach (var member in implicitModel.Members)
            {
                var assign = GetMemberAssignment(context, member, safeName, stateVar, assignmentIndent);
                if (!string.IsNullOrEmpty(assign))
                {
                    assignments.Add($"{assignmentIndent}{assign}");
                }
            }

            if (assignments.Count > 0)
            {
                sb.AppendLine(string.Join(",\n", assignments));
            }

            if (!implicitModel.IsStruct && !skipNullCheck)
            {
                sb.AppendLine("                };");
            }
            else
            {
                sb.AppendLine("            };");
            }

            if (!implicitModel.IsStruct && !skipNullCheck)
            {
                sb.AppendLine("            }");
            }

            return sb.ToString().TrimEnd();
        }

        internal static string GetImplicitCloneExpression(CloneGeneratorContext context,
                                                          TypeModel implicitModel,
                                                          string sourceVar,
                                                          string stateVar,
                                                          string indent = "            ",
                                                          bool isMemberNullable = true)
        {
            var isSimpleIdentifier = System.Text.RegularExpressions.Regex.IsMatch(sourceVar, "^[a-zA-Z0-9_]+$");

            var variableName = sourceVar;
            var wrapperPrefix = "";
            var wrapperSuffix = "";

            var skipNullCheck = context.Model.TrustNullability && !isMemberNullable;

            if (!isSimpleIdentifier)
            {
                var safeBase = System.Text.RegularExpressions.Regex.Replace(sourceVar, "[^a-zA-Z0-9_]", "_");
                safeBase = System.Text.RegularExpressions.Regex.Replace(safeBase, "_+", "_").Trim('_');
                var safeName = $"l_{safeBase}_{context.GetNextVariableId()}";

                variableName = safeName;

                if (implicitModel.IsStruct)
                {
                    wrapperPrefix = $"({sourceVar} is var {variableName} ? ";
                    wrapperSuffix = $" : default({implicitModel.FullyQualifiedName}))";
                }
                else
                {
                    if (skipNullCheck)
                    {
                        wrapperPrefix = $"({sourceVar} is var {variableName} ? ";
                        wrapperSuffix = " : null!)";
                    }
                    else
                    {
                        wrapperPrefix = $"({sourceVar} is var {variableName} && {variableName} != null ? ";
                        wrapperSuffix = " : null)";
                    }
                }
            }
            else
            {
                if (!implicitModel.IsStruct && !skipNullCheck)
                {
                    wrapperPrefix = $"{variableName} == null ? null : ";
                    wrapperSuffix = "";
                }
            }

            var typeName = implicitModel.FullyQualifiedName;
            var innerIndent = indent + "    ";

            List<string> assignments = [ ];

            foreach (var member in implicitModel.Members)
            {
                var assign = GetMemberAssignment(context, member, variableName, stateVar, innerIndent);
                if (!string.IsNullOrEmpty(assign))
                {
                    assignments.Add(assign);
                }
            }

            var init = assignments.Count > 0 ?
                $"new {typeName}\n{indent}{{\n{string.Join(",\n", assignments.Select(a => $"{indent}    {a}"))}\n{indent}}}" :
                $"new {typeName}()";

            if (implicitModel.IsStruct && isSimpleIdentifier)
            {
                return init;
            }

            return wrapperPrefix + init + wrapperSuffix;
        }

        private static string GetHelperMethodCall(CloneGeneratorContext context, string methodName, string sourceExpression, bool needsState, string stateVar = "null")
        {
            var typeParams = GetTypeParametersString(context.Model);
            return needsState ? $"{methodName}{typeParams}({sourceExpression}, {stateVar})" : $"{methodName}{typeParams}({sourceExpression})";
        }

        private static string GetTypeParametersString(TypeModel model) => model.TypeParameters.Count == 0 ? string.Empty : $"<{string.Join(", ", model.TypeParameters)}>";

        private static string GetTypeNameFromFullName(string fullName)
        {
            var lastDot = fullName.LastIndexOf('.');
            if (lastDot >= 0 && lastDot < fullName.Length - 1)
            {
                return fullName.Substring(lastDot + 1);
            }

            return fullName;
        }

        /// <summary>
        /// Returns the precomputed extension class FQN for a clonable member type.
        /// </summary>
        private static string GetExtensionClassName(MemberModel member) => member.ClonableExtensionClass!;

        /// <summary>
        /// Returns the precomputed extension class FQN for a clonable collection element type.
        /// </summary>
        public static string GetExtensionClassNameForType(MemberModel member) => member.ElementClonableExtensionClass!;
    }
}