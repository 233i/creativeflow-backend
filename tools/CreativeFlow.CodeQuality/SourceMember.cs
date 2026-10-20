namespace CreativeFlow.CodeQuality;

/// <summary>一项手写声明及其注释状态，用于命令行检查和治理盘点。</summary>
/// <param name="File">相对仓库根目录的源文件路径。</param>
/// <param name="Line">声明起始的一基行号。</param>
/// <param name="Start">声明在源文件中的 UTF-16 起始偏移。</param>
/// <param name="ParentStart">位置记录参数所属声明的起始偏移，其他声明使用自身偏移。</param>
/// <param name="Kind">方法、属性、字段、记录参数或枚举成员等声明种类。</param>
/// <param name="Name">源代码中的成员名称。</param>
/// <param name="Owner">包含该成员的类型名称。</param>
/// <param name="Type">返回值或字段的源代码类型。</param>
/// <param name="Bases">包含类型直接声明的基类和接口。</param>
/// <param name="InterfaceMember">是否直接声明在接口内。</param>
/// <param name="Documented">是否存在有正文的 summary、对应的记录参数说明或局部函数普通说明。</param>
/// <param name="NeedsLogicComment">有较长控制流的方法是否缺少内部说明。</param>
/// <param name="BodyStart">方法块左花括号之后的 UTF-16 偏移，无块时为空。</param>
/// <param name="Signature">用于盘点的声明片段。</param>
public sealed record SourceMember(
    string File,
    int Line,
    int Start,
    int ParentStart,
    string Kind,
    string Name,
    string Owner,
    string Type,
    string[] Bases,
    bool InterfaceMember,
    bool Documented,
    bool NeedsLogicComment,
    int? BodyStart,
    string Signature);
