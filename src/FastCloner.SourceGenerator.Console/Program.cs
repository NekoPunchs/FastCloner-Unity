using FastCloner.SourceGenerator.Shared;
using WaveKits.Interface;

namespace WaveKits.Interface
{
    /// <summary>
    /// 克隆
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public interface ICloneable<out T> : ICloneable
    {
        T Clone(bool deepClone);
    }
}

namespace FastCloner.SourceGenerator.Console
{
    using System;

    [FastClonerClonable, FastClonerSimulateNoRuntime]
    public partial class Person
    {
        public Action<int> aaaaa;

        public string Name { get; set; }

        public int Age { get; set; }

        [FastClonerBehavior(CloneBehavior.Ignore)]
        public List<Person2> Hobbies { get; set; }
        
        public Person2 Person2 { get; set; }
    }

    public class Person2
    {
    }

    //
    // [FastClonerClonable]
    // [FastClonerSimulateNoRuntime]
    // public class GenericClassWithConstraint<T>
    // {
    //     public T Value { get; set; }
    // }
    //
    // public class SampleUnannotatedClass
    // {
    //     public List<string> StringList { get; set; }
    // }
    //
    // [FastClonerClonable]
    // public class GenericClassWithInclude<T>
    // {
    //     public T Value { get; set; }
    // }

    internal class Program
    {
        private static void Main(string[] args)
        {
            var p = new Person();

            // var myTest = new GenericClassWithConstraint<Dictionary<string, SampleUnannotatedClass>>();
            //
            // var original = new GenericClassWithConstraint<List<int>> { Value = new List<int> { 1, 2, 3 } };

            // var clone = original.FastDeepClone();

            /*Person person = new Person
            {
                Name = "John",
                Age = 30,
                Hobbies = new List<string> { "Reading", "Gaming" }
            };*/

            /*var clone = PersonClone.Clone(person);

            Console.WriteLine($"Original: {person.Name}, {person.Age}");
            Console.WriteLine($"Clone: {clone.Name}, {clone.Age}");*/
        }
    }
}