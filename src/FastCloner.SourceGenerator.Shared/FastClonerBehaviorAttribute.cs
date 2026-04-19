// ***********************************************************************
//  文件名：         FastClonerShallowAttribute.cs
//  创建日期：       2026/04/19
//  作者：           NekoPunch!
//  ***********************************************************************

using System;

namespace FastCloner.SourceGenerator.Shared;

public enum CloneBehavior
{
    /// <summary>
    /// Perform deep cloning (default behavior).
    /// </summary>
    Clone,
    
    /// <summary>
    /// Return the same instance without cloning (for immutable/safe types).
    /// </summary>
    Reference,
    
    /// <summary>
    /// Perform shallow cloning (MemberwiseClone).
    /// </summary>
    Shallow,

    /// <summary>
    /// Skip cloning, return default.
    /// </summary>
    Ignore
}

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Event | AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface)]
public class FastClonerBehaviorAttribute(CloneBehavior behavior) : Attribute
{
    /// <summary>
    /// Gets the cloning behavior.
    /// </summary>
    public CloneBehavior Behavior { get; } = behavior;
}