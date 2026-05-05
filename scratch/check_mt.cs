using MassTransit;
using System.Reflection;

var type = typeof(ConsumeContext<>);
foreach (var method in type.GetMethods())
{
    Console.WriteLine(method.Name);
}
