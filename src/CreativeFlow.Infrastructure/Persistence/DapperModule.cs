// 为此程序集中的每个 Dapper 调用点启用 Dapper.AOT 拦截器生成。
//
// 此特性、Dapper.AOT 包引用和 InterceptorsPreviewNamespaces 属性必须与调用点位于同一项目：
// 拦截器是编译期产物，不会跨越 ProjectReference。来自其他项目的 Dapper 调用会改为针对
// 基于反射的 Dapper 编译，构建时不报错，只在作为原生二进制文件运行时抛错。
[module: Dapper.DapperAot]
