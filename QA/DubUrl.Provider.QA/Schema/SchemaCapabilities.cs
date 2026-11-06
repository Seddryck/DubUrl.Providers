using DubUrl.Schema;
using SchemaModel = DubUrl.Schema.Schema;
namespace DubUrl.Providers.QA.Contracts.Schema;

[Flags]
public enum SchemaCapabilities
{
    None = 0,
    CreateTable = 1,
    DropTableIfExists = 2,
}
