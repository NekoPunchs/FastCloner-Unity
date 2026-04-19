using System.Collections.Generic;
using System.Text;
using Microsoft.CodeAnalysis;

namespace FastCloner.SourceGenerator
{
    internal sealed class CloneGeneratorContext
    {
        public TypeModel Model { get; }

        public StringBuilder Source { get; } = new();

        private readonly Dictionary<string, string> _typeNameToMethodName;
        private readonly HashSet<string> _neededHelperMethods;
        private readonly Queue<string> _pendingHelperMethods = new();
        private readonly Dictionary<string, MemberModel> _typeNameToMemberModel = new();
        private readonly Dictionary<string, TypeModel> _implicitTypeModels = new();
        private readonly Dictionary<string, TypeModel> _derivedTypeHelpers = new();
        private readonly Dictionary<string, int> _helperUsageCounts = new();

        public SourceProductionContext Ctx { get; }

        public bool NeedsStateClass { get; set; }

        public bool NeedsClonerClass { get; set; }

        public bool UseStaticMethods { get; set; } = true;

        public bool CanHaveCircularReferences { get; set; }

        public bool NeedsStateTracking { get; set; }

        public bool IsFastClonerAvailable { get; }

        public TargetFramework TargetFramework { get; }

        private readonly Dictionary<string, bool> _circularReferenceOverrides = new();

        public CloneGeneratorContext(SourceProductionContext ctx, TypeModel model, Dictionary<string, string>? sharedMethodNames = null, HashSet<string>? sharedNeededHelpers = null)
        {
            Ctx = ctx;
            Model = model;
            CanHaveCircularReferences = model.CanHaveCircularReferences;
            IsFastClonerAvailable = model.IsFastClonerAvailable;
            TargetFramework = model.TargetFramework;

            var anyMemberNeedsIdentity = false;
            foreach (var m in model.Members)
            {
                if (m.PreserveIdentity == true)
                {
                    anyMemberNeedsIdentity = true;
                    break;
                }
            }

            NeedsStateTracking = model.NeedsStateTracking || anyMemberNeedsIdentity;

            _typeNameToMethodName = sharedMethodNames ?? new Dictionary<string, string>();
            _neededHelperMethods = sharedNeededHelpers ?? [ ];

            foreach (var related in model.RelatedTypes)
            {
                _implicitTypeModels[related.FullyQualifiedName] = related;
            }

            foreach (var nested in model.NestedTypes)
            {
                if (!_typeNameToMemberModel.ContainsKey(nested.TypeFullName))
                {
                    _typeNameToMemberModel[nested.TypeFullName] = nested;
                }
            }
        }

        public void SetCircularReferenceOverride(string typeName, bool needsState) => _circularReferenceOverrides[typeName] = needsState;

        public bool NeedsCircularState(string typeName, bool defaultFromModel)
        {
            if (_circularReferenceOverrides.TryGetValue(typeName, out var overrideValue))
            {
                return overrideValue;
            }

            return defaultFromModel;
        }

        public bool HasPendingHelperMethods => _pendingHelperMethods.Count > 0;

        public string DequeuePendingHelperMethod() => _pendingHelperMethods.Dequeue();

        public bool TryGetImplicitTypeModel(string typeName, out TypeModel model) => _implicitTypeModels.TryGetValue(typeName, out model);

        public bool TryGetMemberModel(string typeName, out MemberModel model) => _typeNameToMemberModel.TryGetValue(typeName, out model);

        public string GetMethodName(string typeName) => _typeNameToMethodName[typeName];

        public void RegisterImplicitType(TypeModel model)
        {
            if (!_implicitTypeModels.ContainsKey(model.FullyQualifiedName))
            {
                _implicitTypeModels[model.FullyQualifiedName] = model;
            }
        }

        public void RegisterExternalMethod(string typeFullName, string methodName) => _typeNameToMethodName[typeFullName] = methodName;

        public string GetOrCreateHelperMethodName(string typeFullName)
        {
            if (_typeNameToMethodName.TryGetValue(typeFullName, out var existingMethod))
            {
                return existingMethod;
            }

            var methodName = $"FastClonerSgClone{GetCleanTypeName(typeFullName)}";
            _typeNameToMethodName[typeFullName] = methodName;

            if (_neededHelperMethods.Add(typeFullName))
            {
                _pendingHelperMethods.Enqueue(typeFullName);
            }

            return methodName;
        }

        public string GetOrCreateHelperMethodName(MemberModel member)
        {
            var typeKey = member.TypeFullName;

            if (_typeNameToMethodName.TryGetValue(typeKey, out var existingMethod))
            {
                return existingMethod;
            }

            var methodName = $"FastClonerSgClone{GetCleanTypeName(member.TypeFullName)}";
            _typeNameToMethodName[typeKey] = methodName;

            if (_neededHelperMethods.Add(typeKey))
            {
                _pendingHelperMethods.Enqueue(typeKey);
            }

            if (!_typeNameToMemberModel.ContainsKey(typeKey))
            {
                _typeNameToMemberModel[typeKey] = member;
            }

            return methodName;
        }

        private static string GetCleanTypeName(string typeName) =>
            typeName.Replace("global::", "").
                     Replace('<', '_').
                     Replace('>', '_').
                     Replace(',', '_').
                     Replace(' ', '_').
                     Replace('.', '_').
                     Replace('[', '_').
                     Replace(']', '_').
                     Replace('?', '_').
                     Replace(':', '_');

        public void RegisterDerivedTypeHelper(TypeModel derivedType, string methodName)
        {
            if (!_derivedTypeHelpers.ContainsKey(derivedType.FullyQualifiedName))
            {
                _derivedTypeHelpers[derivedType.FullyQualifiedName] = derivedType;
                _typeNameToMethodName[derivedType.FullyQualifiedName] = methodName;
            }
        }

        public IEnumerable<(TypeModel Model, string MethodName)> GetDerivedTypeHelpers()
        {
            foreach (var kvp in _derivedTypeHelpers)
            {
                yield return (kvp.Value, _typeNameToMethodName[kvp.Key]);
            }
        }

        public bool HasDerivedTypeHelpers => _derivedTypeHelpers.Count > 0;

        public void IncrementHelperUsage(string typeFullName)
        {
            if (_helperUsageCounts.TryGetValue(typeFullName, out var count))
            {
                _helperUsageCounts[typeFullName] = count + 1;
            }
            else
            {
                _helperUsageCounts[typeFullName] = 1;
            }
        }

        public int GetHelperUsageCount(string typeFullName) => _helperUsageCounts.TryGetValue(typeFullName, out var count) ? count : 0;

        public bool ShouldInline(string typeFullName) => GetHelperUsageCount(typeFullName) == 1;

        private int _variableCounter = 0;
        public int GetNextVariableId() => System.Threading.Interlocked.Increment(ref _variableCounter);

        public static string FastClonerDeepCloneCall(string expression) => $"global::FastCloner.FastCloner.DeepClone({expression})";

        public static string NotNullIfNotNullAttr(bool isAvailable, string paramName = "source") =>
            isAvailable ? $"[return: global::System.Diagnostics.CodeAnalysis.NotNullIfNotNull(\"{paramName}\")]" : "";
    }
}