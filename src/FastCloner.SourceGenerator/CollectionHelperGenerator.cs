using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace FastCloner.SourceGenerator
{
    /// <summary>
    /// Generates helper methods for collections, dictionaries, and arrays.
    /// </summary>
    internal static class CollectionHelperGenerator
    {
        public static void GenerateHelpers(CloneGeneratorContext context)
        {
            // Use queue to handle recursive dependencies (implicit type A might use implicit type B)
            while (context.HasPendingHelperMethods)
            {
                var typeFullName = context.DequeuePendingHelperMethod();

                // Check if it's an implicit type cloner
                if (context.TryGetImplicitTypeModel(typeFullName, out var implicitModel))
                {
                    WriteImplicitCloneMethod(context, implicitModel, context.GetMethodName(typeFullName));
                    continue;
                }

                // Get the member model for this type
                if (!context.TryGetMemberModel(typeFullName, out var member))
                {
                    continue;
                }

                // Generate appropriate helper method based on member type kind
                switch (member.TypeKind)
                {
                    case MemberTypeKind.Array:
                        WriteArrayCloneMethod(context, member);
                    break;

                    case MemberTypeKind.MultiDimArray:
                        WriteMultiDimArrayCloneMethod(context, member);
                    break;

                    case MemberTypeKind.Collection:
                        WriteCollectionCloneMethod(context, member);
                    break;

                    case MemberTypeKind.Dictionary:
                        WriteDictionaryCloneMethod(context, member);
                    break;

                    // Other types don't get helper methods (handled inline or via FastCloner runtime)
                }
            }
        }

        private static void WriteImplicitCloneMethod(CloneGeneratorContext context, TypeModel implicitModel, string methodName)
        {
            var typeName = implicitModel.FullyQualifiedName;

            // Determine if we need state. 
            // If Root can't have circular refs, then we don't pass state down (it's null).
            // If Root CAN, then we check if ImplicitModel CAN.
            // If ImplicitModel CAN, we accept state.
            var needsState = implicitModel.NeedsStateTracking && context.NeedsStateTracking;
            var sb = context.Source;

            if (needsState)
            {
                context.NeedsStateClass = true;
            }

            WriteHelperMethodSignature(context, typeName, methodName, needsState, implicitModel.IsStruct);
            sb.AppendLine("        {");

            // For non-nullable structs, source cannot be null.
            if (!implicitModel.IsStruct)
            {
                sb.AppendLine("            if (source == null) return null;");
            }

            if (needsState)
            {
                sb.AppendLine("            if (state != null)");
                sb.AppendLine("            {");
                sb.AppendLine("                var known = state.GetKnownRef(source);");
                if (implicitModel.IsStruct)
                {
                    sb.AppendLine($"                if (known != null) return ({typeName})known;");
                }
                else
                {
                    sb.AppendLine($"                if (known != null) return ({typeName})known;");
                }

                sb.AppendLine("            }");
                sb.AppendLine();
            }

            if (implicitModel.IsStruct)
            {
                sb.AppendLine($"            var result = source;");
            }
            else
            {
                sb.AppendLine($"            var result = new {typeName}");
                sb.AppendLine("            {");

                List<string> assignments = [ ];
                foreach (var member in implicitModel.Members)
                {
                    // Recursive call to GetMemberAssignment will trigger generation of dependencies
                    var assign = MemberCloneGenerator.GetMemberAssignment(context, member, "source", needsState ? "state" : "null", "                ");
                    if (!string.IsNullOrEmpty(assign))
                    {
                        assignments.Add($"                {assign}");
                    }
                }

                if (assignments.Count > 0)
                {
                    sb.AppendLine(string.Join(",\n", assignments));
                }

                sb.AppendLine("            };");
            }

            if (needsState)
            {
                sb.AppendLine("            state?.AddKnownRef(source, result);");
            }

            // struct logic for members?
            if (implicitModel.IsStruct)
            {
                foreach (var member in implicitModel.Members)
                {
                    // We use WriteMemberCloning to handle assignment statements
                    MemberCloneGenerator.WriteMemberCloning(context, member, "result", "source", needsState ? "state" : "null");
                }
            }

            sb.AppendLine();
            sb.AppendLine("            return result;");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        private static void WriteCollectionCloneMethod(CloneGeneratorContext context, MemberModel member)
        {
            if (member.ElementTypeName == null)
            {
                return;
            }

            var typeName = member.TypeFullName;
            var methodName = context.GetMethodName(typeName);
            var kind = member.CollectionKind;
            var needsState = MemberCloneGenerator.MemberNeedsCircularRefTracking(context, member);
            var isValueType = member.IsValueType;

            // Handle special collections
            if (kind.ToString().StartsWith("Immutable"))
            {
                WriteImmutableCollectionCloneMethod(context, member, typeName, methodName, kind, needsState, isValueType);
                return;
            }

            if (kind == CollectionKind.ReadOnlyCollection)
            {
                WriteReadOnlyCollectionCloneMethod(context, member, typeName, methodName, needsState, isValueType);
                return;
            }

            var isSafe = member.ElementIsSafe;
            var hasClonableAttr = member.ElementHasClonableAttr;
            var sb = context.Source;

            // Use pre-computed collection kind and concrete type
            var concreteType = member.ConcreteTypeFullName ?? typeName;

            // Determine operations based on kind
            var addMethod = "Add";
            var isQueue = kind == CollectionKind.Queue || kind == CollectionKind.ConcurrentQueue;
            var isStack = kind == CollectionKind.Stack || kind == CollectionKind.ConcurrentStack;
            var isLinkedList = kind == CollectionKind.LinkedList;

            if (isQueue)
            {
                addMethod = "Enqueue";
            }
            else if (isStack)
            {
                addMethod = "Push";
            }
            else if (isLinkedList)
            {
                addMethod = "AddLast";
            }

            // Determine if capacity can be passed to constructor.
            // Only standard List, HashSet, Queue, Stack support new T(int capacity).
            // For CollectionKind.List, the source must also have .Count (e.g. IEnumerable<T> does not).
            var supportsCapacity = (kind == CollectionKind.List && member.CollectionHasCount) ||
                                   kind == CollectionKind.HashSet ||
                                   kind == CollectionKind.Queue ||
                                   kind == CollectionKind.Stack;

            if (needsState)
            {
                context.NeedsStateClass = true;
            }

            WriteHelperMethodSignature(context, typeName, methodName, needsState, isValueType);
            sb.AppendLine("        {");

            if (!isValueType)
            {
                sb.AppendLine("            if (source == null) return null;");
            }

            if (needsState)
            {
                sb.AppendLine("            if (state != null)");
                sb.AppendLine("            {");
                sb.AppendLine("                var known = state.GetKnownRef(source);");
                sb.AppendLine($"                if (known != null) return ({typeName})known;");
                sb.AppendLine("            }");
                sb.AppendLine();
            }

            // Optimize: if element type is safe, use IEnumerable constructor directly (works for List, HashSet, etc.)
            // Note: Queue/Stack/LinkedList also support IEnumerable ctor
            if (isSafe && !needsState)
            {
                sb.AppendLine($"            return new {concreteType}(source);");
            }
            else
            {
                // Pre-allocate capacity for common collection types (List, HashSet, etc. support Count)
                if (supportsCapacity)
                {
                    sb.AppendLine($"            var result = new {concreteType}(source.Count);");
                }
                else
                {
                    sb.AppendLine($"            var result = new {concreteType}();");
                }

                if (needsState)
                {
                    sb.AppendLine("            state?.AddKnownRef(source, result);");
                    sb.AppendLine();
                }

                if (isStack)
                {
                    sb.AppendLine("            // Stack iteration is Top->Bottom. Pushing in this order would reverse the stack.");
                    sb.AppendLine("            // We need to push from Bottom->Top.");
                    sb.AppendLine("            var temp = new global::System.Collections.Generic.List<object?>(source.Count);");
                    sb.AppendLine("            foreach (var item in source)");
                    sb.AppendLine("            {");

                    var itemExpr = GetItemCloneExpression(context, member, "item", isSafe, hasClonableAttr, needsState);
                    sb.AppendLine($"                var clonedItem = {itemExpr};");
                    sb.AppendLine("                temp.Add(clonedItem!);");
                    sb.AppendLine("            }");
                    sb.AppendLine("            for (int i = temp.Count - 1; i >= 0; i--)");
                    sb.AppendLine("            {");
                    sb.AppendLine($"                result.Push(({member.ElementTypeName})temp[i]!);");
                    sb.AppendLine("            }");
                }
                else if (kind == CollectionKind.List && member.CollectionHasIndexer)
                {
                    if (context.TargetFramework >= TargetFramework.Net5)
                    {
                        sb.AppendLine("            global::System.Runtime.InteropServices.CollectionsMarshal.SetCount(result, source.Count);");
                        sb.AppendLine("            var span = global::System.Runtime.InteropServices.CollectionsMarshal.AsSpan(result);");
                        sb.AppendLine("            for (int i = 0; i < source.Count; i++)");
                        sb.AppendLine("            {");
                        var itemExpr = GetItemCloneExpression(context, member, "source[i]", isSafe, hasClonableAttr, needsState);
                        sb.AppendLine($"                var clonedItem = {itemExpr};");
                        sb.AppendLine("                span[i] = clonedItem!;");
                        sb.AppendLine("            }");
                    }
                    else
                    {
                        sb.AppendLine("            for (int i = 0; i < source.Count; i++)");
                        sb.AppendLine("            {");
                        var itemExpr = GetItemCloneExpression(context, member, "source[i]", isSafe, hasClonableAttr, needsState);
                        sb.AppendLine($"                var clonedItem = {itemExpr};");
                        sb.AppendLine("                result.Add(clonedItem!);");
                        sb.AppendLine("            }");
                    }
                }
                else
                {
                    sb.AppendLine("            foreach (var item in source)");
                    sb.AppendLine("            {");

                    var itemExpr = GetItemCloneExpression(context, member, "item", isSafe, hasClonableAttr, needsState);

                    sb.AppendLine($"                var clonedItem = {itemExpr};");
                    sb.AppendLine($"                result.{addMethod}(clonedItem!);");
                    sb.AppendLine("            }");
                }

                sb.AppendLine();
                sb.AppendLine("            return result;");
            }

            sb.AppendLine("        }");
            sb.AppendLine();
        }

        private static void WriteImmutableCollectionCloneMethod(CloneGeneratorContext context,
                                                                MemberModel member,
                                                                string typeName,
                                                                string methodName,
                                                                CollectionKind kind,
                                                                bool needsState,
                                                                bool isValueType)
        {
            var sb = context.Source;
            var isSafe = member.ElementIsSafe;
            var hasClonableAttr = member.ElementHasClonableAttr;

            if (needsState)
            {
                context.NeedsStateClass = true;
            }

            WriteHelperMethodSignature(context, typeName, methodName, needsState, isValueType);
            sb.AppendLine("        {");

            if (!isValueType)
            {
                sb.AppendLine("            if (source == null) return null;");
            }

            if (isSafe && !needsState)
            {
                sb.AppendLine("            return source;");
                sb.AppendLine("        }");
                sb.AppendLine();
                return;
            }

            if (needsState)
            {
                sb.AppendLine("            if (state != null)");
                sb.AppendLine("            {");
                sb.AppendLine("                var known = state.GetKnownRef(source);");
                sb.AppendLine($"                if (known != null) return ({typeName})known;");
                sb.AppendLine("            }");
                sb.AppendLine();
            }

            sb.AppendLine($"            var temp = new global::System.Collections.Generic.List<{member.ElementTypeName}>();");

            sb.AppendLine("            foreach (var item in source)");
            sb.AppendLine("            {");
            var itemExpr = GetItemCloneExpression(context, member, "item", isSafe, hasClonableAttr, needsState);
            sb.AppendLine($"                var clonedItem = {itemExpr};");
            sb.AppendLine("                temp.Add(clonedItem!);");
            sb.AppendLine("            }");

            if (kind == CollectionKind.ImmutableStack)
            {
                sb.AppendLine("            temp.Reverse();");
            }

            var factoryMethod = kind switch
                                {
                                    CollectionKind.ImmutableList      => "global::System.Collections.Immutable.ImmutableList.CreateRange(temp)",
                                    CollectionKind.ImmutableArray     => "global::System.Collections.Immutable.ImmutableArray.CreateRange(temp)",
                                    CollectionKind.ImmutableHashSet   => "global::System.Collections.Immutable.ImmutableHashSet.CreateRange(temp)",
                                    CollectionKind.ImmutableSortedSet => "global::System.Collections.Immutable.ImmutableSortedSet.CreateRange(temp)",
                                    CollectionKind.ImmutableQueue     => "global::System.Collections.Immutable.ImmutableQueue.CreateRange(temp)",
                                    CollectionKind.ImmutableStack     => "global::System.Collections.Immutable.ImmutableStack.CreateRange(temp)",
                                    _                                 => "global::System.Collections.Immutable.ImmutableList.CreateRange(temp)" // default fallback
                                };

            sb.AppendLine($"            var result = {factoryMethod};");

            if (needsState)
            {
                sb.AppendLine("            state?.AddKnownRef(source, result);");
            }

            sb.AppendLine("            return result;");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        private static void WriteReadOnlyCollectionCloneMethod(CloneGeneratorContext context,
                                                               MemberModel member,
                                                               string typeName,
                                                               string methodName,
                                                               bool needsState,
                                                               bool isValueType)
        {
            var sb = context.Source;
            var isSafe = member.ElementIsSafe;
            var hasClonableAttr = member.ElementHasClonableAttr;

            if (needsState)
            {
                context.NeedsStateClass = true;
            }

            WriteHelperMethodSignature(context, typeName, methodName, needsState, isValueType);
            sb.AppendLine("        {");

            if (!isValueType)
            {
                sb.AppendLine("            if (source == null) return null;");
            }

            if (needsState)
            {
                sb.AppendLine("            if (state != null)");
                sb.AppendLine("            {");
                sb.AppendLine("                var known = state.GetKnownRef(source);");
                sb.AppendLine($"                if (known != null) return ({typeName})known;");
                sb.AppendLine("            }");
                sb.AppendLine();
            }

            sb.AppendLine($"            var temp = new global::System.Collections.Generic.List<{member.ElementTypeName}>(source.Count);");

            sb.AppendLine("            foreach (var item in source)");
            sb.AppendLine("            {");
            var itemExpr = GetItemCloneExpression(context, member, "item", isSafe, hasClonableAttr, needsState);
            sb.AppendLine($"                var clonedItem = {itemExpr};");
            sb.AppendLine("                temp.Add(clonedItem!);");
            sb.AppendLine("            }");

            sb.AppendLine($"            var result = new global::System.Collections.ObjectModel.ReadOnlyCollection<{member.ElementTypeName}>(temp);");

            if (needsState)
            {
                sb.AppendLine("            state?.AddKnownRef(source, result);");
            }

            sb.AppendLine("            return result;");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        private static string GetItemCloneExpression(CloneGeneratorContext context,
                                                     MemberModel member,
                                                     string itemVar,
                                                     bool isSafe,
                                                     bool hasClonableAttr,
                                                     bool parentNeedsState)
        {
            if (isSafe)
            {
                return itemVar;
            }

            if (member.ElementHasClonableInterface)
            {
                return $"{itemVar}.Clone(true)";
            }

            if (hasClonableAttr && !member.ElementHasClonableInterface)
            {
                // string extensionClassName = MemberCloneGenerator.GetExtensionClassNameForType(member);
                // string stateArg = parentNeedsState ? "state" : "null";
                // return $"{extensionClassName}.InternalFastDeepClone({itemVar}, {stateArg})!";
                context.Ctx.ReportDiagnostic(Diagnostic.Create(new DiagnosticDescriptor("FCG100",
                                                                                        "Request WaveKits.ICloneable",
                                                                                        "Type '{0}' request WaveKits.ICloneable impl.",
                                                                                        "FastCloner",
                                                                                        DiagnosticSeverity.Error,
                                                                                        true),
                                                               Location.None,
                                                               member.Name));

                return "";
            }

            if (context.TryGetMemberModel(member.ElementTypeName!, out var nestedModel))
            {
                var helperName = context.GetOrCreateHelperMethodName(nestedModel);
                var elementNeedsState = MemberCloneGenerator.MemberNeedsCircularRefTracking(context, nestedModel);
                var actualStateVar = parentNeedsState ? "state" : "null";
                return GetHelperMethodCall(context, helperName, itemVar, elementNeedsState, actualStateVar);
            }

            if (context.TryGetImplicitTypeModel(member.ElementTypeName!, out var implicitModel))
            {
                if (context.ShouldInline(implicitModel.FullyQualifiedName))
                {
                    var actualStateVarInline = parentNeedsState ? "state" : "null";
                    return MemberCloneGenerator.GetImplicitCloneExpression(context, implicitModel, itemVar, actualStateVarInline, "                ", true) + "!";
                }

                var helperName = context.GetOrCreateHelperMethodName(implicitModel.FullyQualifiedName);
                var elementNeedsState = implicitModel.NeedsStateTracking && context.NeedsStateTracking;
                var actualStateVar = parentNeedsState ? "state" : "null";
                return GetHelperMethodCall(context, helperName, itemVar, elementNeedsState, actualStateVar);
            }

            return context.IsFastClonerAvailable ?
                $"({member.ElementTypeName}){CloneGeneratorContext.FastClonerDeepCloneCall(itemVar)}!" :
                itemVar;
        }

        private static void WriteDictionaryCloneMethod(CloneGeneratorContext context, MemberModel member)
        {
            if (member.KeyTypeName == null || member.ValueTypeName == null)
            {
                return;
            }

            var typeName = member.TypeFullName;
            var methodName = context.GetMethodName(typeName);
            var kind = member.CollectionKind;
            var needsState = MemberCloneGenerator.MemberNeedsCircularRefTracking(context, member);
            var isValueType = member.IsValueType;

            if (kind.ToString().StartsWith("Immutable"))
            {
                WriteImmutableDictionaryCloneMethod(context, member, typeName, methodName, kind, needsState, isValueType);
                return;
            }

            if (kind == CollectionKind.ReadOnlyDictionary)
            {
                WriteReadOnlyDictionaryCloneMethod(context, member, typeName, methodName, needsState, isValueType);
                return;
            }

            var sb = context.Source;
            var concreteType = member.ConcreteTypeFullName ?? typeName;
            var supportsCapacity = kind is CollectionKind.Dictionary or CollectionKind.SortedList or CollectionKind.List or CollectionKind.None;
            var isConcurrent = kind == CollectionKind.ConcurrentDictionary;
            var keysAreSafe = member.KeyIsSafe;
            var valuesAreSafe = member.ValueIsSafe;

            if (needsState)
            {
                context.NeedsStateClass = true;
            }

            WriteHelperMethodSignature(context, typeName, methodName, needsState, isValueType);
            sb.AppendLine("        {");

            if (!isValueType)
            {
                sb.AppendLine("            if (source == null) return null;");
            }

            if (keysAreSafe && valuesAreSafe && !needsState && kind == CollectionKind.Dictionary)
            {
                sb.AppendLine($"            return new {concreteType}(source);");
                sb.AppendLine("        }");
                sb.AppendLine();
                return;
            }

            if (needsState)
            {
                sb.AppendLine("            if (state != null)");
                sb.AppendLine("            {");
                sb.AppendLine("                var known = state.GetKnownRef(source);");
                sb.AppendLine($"                if (known != null) return ({typeName})known;");
                sb.AppendLine("            }");
                sb.AppendLine();
            }

            if (supportsCapacity)
            {
                sb.AppendLine($"            var result = new {concreteType}(source.Count);");
            }
            else
            {
                sb.AppendLine($"            var result = new {concreteType}();");
            }

            if (needsState)
            {
                sb.AppendLine("            state?.AddKnownRef(source, result);");
                sb.AppendLine();
            }

            sb.AppendLine("            foreach (var kvp in source)");
            sb.AppendLine("            {");

            var (keyExpr, valExpr) = GetKeyValueExpressions(context, member, needsState);
            sb.AppendLine($"                var clonedKey = {keyExpr};");
            sb.AppendLine($"                var clonedValue = {valExpr};");

            if (isConcurrent)
            {
                sb.AppendLine("                result.TryAdd(clonedKey!, clonedValue!);");
            }
            else
            {
                sb.AppendLine("                result.Add(clonedKey!, clonedValue!);");
            }

            sb.AppendLine("            }");
            sb.AppendLine();
            sb.AppendLine("            return result;");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        private static void WriteImmutableDictionaryCloneMethod(CloneGeneratorContext context,
                                                                MemberModel member,
                                                                string typeName,
                                                                string methodName,
                                                                CollectionKind kind,
                                                                bool needsState,
                                                                bool isValueType)
        {
            var sb = context.Source;
            var keysAreSafe = member.KeyIsSafe;
            var valuesAreSafe = member.ValueIsSafe;

            if (needsState)
            {
                context.NeedsStateClass = true;
            }

            WriteHelperMethodSignature(context, typeName, methodName, needsState, isValueType);
            sb.AppendLine("        {");

            if (!isValueType)
            {
                sb.AppendLine("            if (source == null) return null;");
            }

            if (keysAreSafe && valuesAreSafe && !needsState)
            {
                sb.AppendLine("            return source;");
                sb.AppendLine("        }");
                sb.AppendLine();
                return;
            }

            if (needsState)
            {
                sb.AppendLine("            if (state != null)");
                sb.AppendLine("            {");
                sb.AppendLine("                var known = state.GetKnownRef(source);");
                sb.AppendLine($"                if (known != null) return ({typeName})known;");
                sb.AppendLine("            }");
                sb.AppendLine();
            }

            sb.AppendLine($"            var temp = new global::System.Collections.Generic.Dictionary<{member.KeyTypeName}, {member.ValueTypeName}>(source.Count);");

            sb.AppendLine("            foreach (var kvp in source)");
            sb.AppendLine("            {");
            var (keyExpr, valExpr) = GetKeyValueExpressions(context, member, needsState);
            sb.AppendLine($"                var clonedKey = {keyExpr};");
            sb.AppendLine($"                var clonedValue = {valExpr};");
            sb.AppendLine("                temp.Add(clonedKey!, clonedValue!);");
            sb.AppendLine("            }");

            var factoryMethod = kind == CollectionKind.ImmutableSortedDictionary ?
                "global::System.Collections.Immutable.ImmutableSortedDictionary.CreateRange(temp)" :
                "global::System.Collections.Immutable.ImmutableDictionary.CreateRange(temp)";

            sb.AppendLine($"            var result = {factoryMethod};");

            if (needsState)
            {
                sb.AppendLine("            state?.AddKnownRef(source, result);");
            }

            sb.AppendLine("            return result;");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        private static void WriteReadOnlyDictionaryCloneMethod(CloneGeneratorContext context,
                                                               MemberModel member,
                                                               string typeName,
                                                               string methodName,
                                                               bool needsState,
                                                               bool isValueType)
        {
            var sb = context.Source;
            if (needsState)
            {
                context.NeedsStateClass = true;
            }

            WriteHelperMethodSignature(context, typeName, methodName, needsState, isValueType);
            sb.AppendLine("        {");

            if (!isValueType)
            {
                sb.AppendLine("            if (source == null) return null;");
            }

            if (needsState)
            {
                sb.AppendLine("            if (state != null)");
                sb.AppendLine("            {");
                sb.AppendLine("                var known = state.GetKnownRef(source);");
                sb.AppendLine($"                if (known != null) return ({typeName})known;");
                sb.AppendLine("            }");
                sb.AppendLine();
            }

            sb.AppendLine($"            var temp = new global::System.Collections.Generic.Dictionary<{member.KeyTypeName}, {member.ValueTypeName}>(source.Count);");

            sb.AppendLine("            foreach (var kvp in source)");
            sb.AppendLine("            {");
            var (keyExpr, valExpr) = GetKeyValueExpressions(context, member, needsState);
            sb.AppendLine($"                var clonedKey = {keyExpr};");
            sb.AppendLine($"                var clonedValue = {valExpr};");
            sb.AppendLine("                temp.Add(clonedKey!, clonedValue!);");
            sb.AppendLine("            }");

            sb.AppendLine($"            var result = new global::System.Collections.ObjectModel.ReadOnlyDictionary<{member.KeyTypeName}, {member.ValueTypeName}>(temp);");

            if (needsState)
            {
                sb.AppendLine("            state?.AddKnownRef(source, result);");
            }

            sb.AppendLine("            return result;");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        private static (string keyExpr, string valExpr) GetKeyValueExpressions(CloneGeneratorContext context, MemberModel member, bool needsState)
        {
            var keyExpr = "kvp.Key";
            if (!member.KeyIsSafe)
            {
                if (member.KeyIsClonable)
                {
                    keyExpr = "kvp.Key?.FastDeepClone()!";
                }
                else if (context.TryGetMemberModel(member.KeyTypeName!, out var nestedKeyModel))
                {
                    var helperName = context.GetOrCreateHelperMethodName(nestedKeyModel);
                    var keyNeedsState = MemberCloneGenerator.MemberNeedsCircularRefTracking(context, nestedKeyModel);
                    var actualStateVar = needsState ? "state" : "null";
                    keyExpr = GetHelperMethodCall(context, helperName, "kvp.Key", keyNeedsState, actualStateVar);
                }
                else if (context.TryGetImplicitTypeModel(member.KeyTypeName!, out var implicitKeyModel))
                {
                    if (context.ShouldInline(implicitKeyModel.FullyQualifiedName))
                    {
                        var actualStateVar = needsState ? "state" : "null";
                        keyExpr = MemberCloneGenerator.GetImplicitCloneExpression(context, implicitKeyModel, "kvp.Key", actualStateVar, "                ", true) + "!";
                    }
                    else
                    {
                        var helperName = context.GetOrCreateHelperMethodName(implicitKeyModel.FullyQualifiedName);
                        var keyNeedsState = implicitKeyModel.NeedsStateTracking && context.NeedsStateTracking;
                        var actualStateVar = needsState ? "state" : "null";
                        keyExpr = GetHelperMethodCall(context, helperName, "kvp.Key", keyNeedsState, actualStateVar);
                    }
                }
                else if (context.IsFastClonerAvailable)
                {
                    keyExpr = $"({member.KeyTypeName}){CloneGeneratorContext.FastClonerDeepCloneCall("kvp.Key")}!";
                }
            }

            var valExpr = "kvp.Value";
            if (!member.ValueIsSafe)
            {
                if (member.ValueIsClonable)
                {
                    valExpr = "kvp.Value?.FastDeepClone()!";
                }
                else if (context.TryGetMemberModel(member.ValueTypeName!, out var nestedValModel))
                {
                    var helperName = context.GetOrCreateHelperMethodName(nestedValModel);
                    var valNeedsState = MemberCloneGenerator.MemberNeedsCircularRefTracking(context, nestedValModel);
                    var actualStateVar = needsState ? "state" : "null";
                    valExpr = GetHelperMethodCall(context, helperName, "kvp.Value", valNeedsState, actualStateVar);
                }
                else if (context.TryGetImplicitTypeModel(member.ValueTypeName!, out var implicitValModel))
                {
                    if (context.ShouldInline(implicitValModel.FullyQualifiedName))
                    {
                        var actualStateVar = needsState ? "state" : "null";
                        valExpr = MemberCloneGenerator.GetImplicitCloneExpression(context, implicitValModel, "kvp.Value", actualStateVar, "                ", true) + "!";
                    }
                    else
                    {
                        var helperName = context.GetOrCreateHelperMethodName(implicitValModel.FullyQualifiedName);
                        var valNeedsState = implicitValModel.NeedsStateTracking && context.NeedsStateTracking;
                        var actualStateVar = needsState ? "state" : "null";
                        valExpr = GetHelperMethodCall(context, helperName, "kvp.Value", valNeedsState, actualStateVar);
                    }
                }
                else if (context.IsFastClonerAvailable)
                {
                    valExpr = $"({member.ValueTypeName}){CloneGeneratorContext.FastClonerDeepCloneCall("kvp.Value")}!";
                }
            }

            return (keyExpr, valExpr);
        }

        private static void WriteArrayCloneMethod(CloneGeneratorContext context, MemberModel member)
        {
            if (member.ElementTypeName == null)
            {
                return;
            }

            var typeName = member.TypeFullName;
            var methodName = context.GetMethodName(typeName);
            var isSafe = member.ElementIsSafe;
            var hasClonableAttr = member.ElementHasClonableAttr;
            var needsState = MemberCloneGenerator.MemberNeedsCircularRefTracking(context, member);
            var sb = context.Source;

            if (needsState)
            {
                context.NeedsStateClass = true;
            }

            WriteHelperMethodSignature(context, typeName, methodName, needsState, false);
            sb.AppendLine("        {");
            sb.AppendLine("            if (source == null) return null;");

            if (needsState)
            {
                sb.AppendLine("            if (state != null)");
                sb.AppendLine("            {");
                sb.AppendLine("                var known = state.GetKnownRef(source);");
                sb.AppendLine($"                if (known != null) return ({typeName})known;");
                sb.AppendLine("            }");
                sb.AppendLine();
            }

            var arrayCreationExpr = GetArrayCreationExpression(member.ElementTypeName, "source.Length");
            sb.AppendLine($"            var result = {arrayCreationExpr};");

            if (needsState)
            {
                sb.AppendLine("            state?.AddKnownRef(source, result);");
                sb.AppendLine();
            }

            if (isSafe)
            {
                sb.AppendLine("            global::System.Array.Copy(source, result, source.Length);");
            }
            else
            {
                sb.AppendLine("            for (int i = 0; i < source.Length; i++)");
                sb.AppendLine("            {");

                string itemExpr;

                if (hasClonableAttr)
                {
                    itemExpr = "source[i]?.FastDeepClone()!";
                }
                else if (context.TryGetMemberModel(member.ElementTypeName!, out var nestedModel))
                {
                    var helperName = context.GetOrCreateHelperMethodName(nestedModel);
                    var elementNeedsState = MemberCloneGenerator.MemberNeedsCircularRefTracking(context, nestedModel);
                    var actualStateVar = needsState ? "state" : "null";
                    itemExpr = GetHelperMethodCall(context, helperName, "source[i]", elementNeedsState, actualStateVar);
                }
                else if (context.TryGetImplicitTypeModel(member.ElementTypeName!, out var implicitModel))
                {
                    if (context.ShouldInline(implicitModel.FullyQualifiedName))
                    {
                        var actualStateVar = needsState ? "state" : "null";
                        itemExpr = MemberCloneGenerator.GetImplicitCloneExpression(context, implicitModel, "source[i]", actualStateVar, "                ", true) + "!";
                    }
                    else
                    {
                        var helperName = context.GetOrCreateHelperMethodName(implicitModel.FullyQualifiedName);
                        var elementNeedsState = implicitModel.NeedsStateTracking && context.NeedsStateTracking;
                        var actualStateVar = needsState ? "state" : "null";
                        itemExpr = GetHelperMethodCall(context, helperName, "source[i]", elementNeedsState, actualStateVar);
                    }
                }
                else if (context.IsFastClonerAvailable)
                {
                    itemExpr = $"({member.ElementTypeName}){CloneGeneratorContext.FastClonerDeepCloneCall("source[i]")}!";
                }
                else
                {
                    itemExpr = "source[i]";
                }

                sb.AppendLine($"                var clonedItem = {itemExpr};");
                sb.AppendLine("                result[i] = clonedItem!;");
                sb.AppendLine("            }");
            }

            sb.AppendLine();
            sb.AppendLine("            return result;");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        private static void WriteMultiDimArrayCloneMethod(CloneGeneratorContext context, MemberModel member)
        {
            if (member.ElementTypeName == null)
            {
                return;
            }

            var typeName = member.TypeFullName;
            var methodName = context.GetMethodName(typeName);
            var isSafe = member.ElementIsSafe;
            var hasClonableAttr = member.ElementHasClonableAttr;
            var needsState = MemberCloneGenerator.MemberNeedsCircularRefTracking(context, member);
            var rank = member.ArrayRank;
            var sb = context.Source;

            if (needsState)
            {
                context.NeedsStateClass = true;
            }

            WriteHelperMethodSignature(context, typeName, methodName, needsState, false);
            sb.AppendLine("        {");
            sb.AppendLine("            if (source == null) return null;");

            if (needsState)
            {
                sb.AppendLine("            if (state != null)");
                sb.AppendLine("            {");
                sb.AppendLine("                var known = state.GetKnownRef(source);");
                sb.AppendLine($"                if (known != null) return ({typeName})known;");
                sb.AppendLine("            }");
                sb.AppendLine();
            }

            for (var d = 0; d < rank; d++)
            {
                sb.AppendLine($"            int len{d} = source.GetLength({d});");
            }

            sb.AppendLine();

            var dimList = string.Join(", ", Enumerable.Range(0, rank).Select(d => $"len{d}"));
            sb.AppendLine($"            var result = new {member.ElementTypeName}[{dimList}];");

            if (needsState)
            {
                sb.AppendLine("            state?.AddKnownRef(source, result);");
            }

            sb.AppendLine();

            if (isSafe)
            {
                sb.AppendLine("            global::System.Array.Copy(source, result, source.Length);");
            }
            else
            {
                for (var d = 0; d < rank; d++)
                {
                    var indent = new string(' ', 12 + d * 4);
                    sb.AppendLine($"{indent}for (int i{d} = 0; i{d} < len{d}; i{d}++)");
                    sb.AppendLine($"{indent}{{");
                }

                var innerIndent = new string(' ', 12 + rank * 4);
                var indexList = string.Join(", ", Enumerable.Range(0, rank).Select(d => $"i{d}"));

                string itemExpr;
                if (hasClonableAttr)
                {
                    itemExpr = $"source[{indexList}]?.FastDeepClone()!";
                }
                else if (context.TryGetMemberModel(member.ElementTypeName!, out var nestedModel))
                {
                    var helperName = context.GetOrCreateHelperMethodName(nestedModel);
                    var elementNeedsState = MemberCloneGenerator.MemberNeedsCircularRefTracking(context, nestedModel);
                    var actualStateVar = needsState ? "state" : "null";
                    itemExpr = GetHelperMethodCall(context, helperName, $"source[{indexList}]", elementNeedsState, actualStateVar);
                }
                else if (context.TryGetImplicitTypeModel(member.ElementTypeName!, out var implicitModel))
                {
                    if (context.ShouldInline(implicitModel.FullyQualifiedName))
                    {
                        var actualStateVar = needsState ? "state" : "null";
                        itemExpr = MemberCloneGenerator.GetImplicitCloneExpression(context, implicitModel, $"source[{indexList}]", actualStateVar, "                ", true) + "!";
                    }
                    else
                    {
                        var helperName = context.GetOrCreateHelperMethodName(implicitModel.FullyQualifiedName);
                        var elementNeedsState = implicitModel.NeedsStateTracking && context.NeedsStateTracking;
                        var actualStateVar = needsState ? "state" : "null";
                        itemExpr = GetHelperMethodCall(context, helperName, $"source[{indexList}]", elementNeedsState, actualStateVar);
                    }
                }
                else if (context.IsFastClonerAvailable)
                {
                    itemExpr = $"({member.ElementTypeName}){CloneGeneratorContext.FastClonerDeepCloneCall($"source[{indexList}]")}!";
                }
                else
                {
                    itemExpr = $"source[{indexList}]";
                }

                sb.AppendLine($"{innerIndent}var clonedItem = {itemExpr};");
                sb.AppendLine($"{innerIndent}result[{indexList}] = clonedItem!;");

                for (var d = rank - 1; d >= 0; d--)
                {
                    var indent = new string(' ', 12 + d * 4);
                    sb.AppendLine($"{indent}}}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("            return result;");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        private static string GetArrayCreationExpression(string elementTypeName, string sizeExpression)
        {
            var bracketIndex = elementTypeName.IndexOf('[');

            if (bracketIndex >= 0)
            {
                var baseType = elementTypeName.Substring(0, bracketIndex);
                var trailingBrackets = elementTypeName.Substring(bracketIndex);
                return $"new {baseType}[{sizeExpression}]{trailingBrackets}";
            }

            return $"new {elementTypeName}[{sizeExpression}]";
        }

        private static void WriteHelperMethodSignature(CloneGeneratorContext context, string typeName, string methodName, bool needsState, bool isValueType)
        {
            var typeParams = GetTypeParametersString(context.Model);
            var constraints = GetTypeConstraintsString(context.Model);
            var sb = context.Source;

            var typeSuffix = "?";
            if (isValueType)
            {
                typeSuffix = "";
            }
            else
            {
                if (typeName.EndsWith("?"))
                {
                    typeSuffix = "";
                }
            }

            var staticMod = context.UseStaticMethods ? "static " : "";

            if (needsState)
            {
                sb.AppendLine($"        private {staticMod}{typeName}{typeSuffix} {methodName}{typeParams}({typeName}{typeSuffix} source, FcGeneratedCloneState? state){constraints}");
            }
            else
            {
                sb.AppendLine($"        private {staticMod}{typeName}{typeSuffix} {methodName}{typeParams}({typeName}{typeSuffix} source){constraints}");
            }
        }

        private static string GetHelperMethodCall(CloneGeneratorContext context, string methodName, string sourceExpression, bool needsState, string stateVar = "null")
        {
            var typeParams = GetTypeParametersString(context.Model);

            if (needsState)
            {
                return $"{methodName}{typeParams}({sourceExpression}, {stateVar})!";
            }

            return $"{methodName}{typeParams}({sourceExpression})!";
        }

        private static string GetTypeParametersString(TypeModel model) => model.TypeParameters.Count == 0 ? string.Empty : $"<{string.Join(", ", model.TypeParameters)}>";

        private static string GetTypeConstraintsString(TypeModel model)
        {
            if (model.TypeConstraints.Count == 0)
            {
                return string.Empty;
            }

            return " " + string.Join(" ", model.TypeConstraints);
        }
    }
}