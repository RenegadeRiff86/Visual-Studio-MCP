using System.Text.RegularExpressions;

namespace VsIdeBridge.Diagnostics;

internal static class ErrorListPatterns
{
    public static readonly Regex ExplicitCodePattern = new(
        @"\b(?:LINK|LNK|MSB|VCR|E|C)\d+\b|\blnt-[a-z0-9-]+\b|\bInt-make\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    public static readonly Regex MsBuildDiagnosticPattern = new(
        @"^(?<file>[A-Za-z]:\\.*?|\S.*?)(?:\((?<line>\d+)(?:,(?<column>\d+))?\))?\s*:\s*(?<severity>warning|error)\s+(?<code>[A-Za-z]+[A-Za-z0-9-]*)\s*:\s*(?<message>.+?)(?:\s+\[(?<project>[^\]]+)\])?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    public static readonly Regex StructuredOutputPattern = new(
        @"^(?<project>.+?)\s*>\s*(?<file>[A-Za-z]:\\.*?|\S.*?)(?:\((?<line>\d+)(?:,(?<column>\d+))?\))?\s*:\s*(?<severity>warning|error)\s+(?<code>[A-Za-z]+[A-Za-z0-9-]*)\s*:\s*(?<message>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    public static readonly Regex StringLiteralPattern = new("\"([^\"\\r\\n]{8,})\"", RegexOptions.Compiled);
    public static readonly Regex ConstStringDeclPattern = new(@"\bconst\s+string\s+\w+\s*=", RegexOptions.Compiled);
    public static readonly Regex NumberLiteralPattern = new(@"(?<![A-Za-z0-9_\.])(?<value>-?\d+(?:\.\d+)?)\b", RegexOptions.Compiled);
    public static readonly Regex SuspiciousRoundDownPattern = new(@"\(int\)\s*Math\s*\.\s*(?<op>Floor|Truncate)\s*\(", RegexOptions.Compiled);
    public static readonly Regex EmptyCatchBlockPattern = new(@"catch\s*(?:\([^)]*\))?\s*\{\s*\}", RegexOptions.Compiled);
    public static readonly Regex AsyncVoidPattern = new(@"\basync\s+void\s+(\w+)", RegexOptions.Compiled);
    public static readonly Regex RawDeletePattern = new(@"\bdelete\s*(\[\])?\s", RegexOptions.Compiled);
    public static readonly Regex UsingNamespacePattern = new(@"^\s*using\s+namespace\s+([\w:]+)\s*;", RegexOptions.Compiled | RegexOptions.Multiline);
    public static readonly Regex CStyleCastPattern = new(@"\((?:const\s+)?(?:unsigned\s+)?(?:int|long|short|char|float|double|size_t|uint\d+_t|int\d+_t|void)\s*\*?\)\s*[a-zA-Z_\(]", RegexOptions.Compiled);
    public static readonly Regex BareExceptPattern = new(@"^\s*except\s*:", RegexOptions.Compiled | RegexOptions.Multiline);
    public static readonly Regex MutableDefaultArgPattern = new(@"\bdef\s+(\w+)\s*\([^)]*=\s*(?:\[\s*\]|\{\s*\}|set\s*\(\s*\))", RegexOptions.Compiled);
    public static readonly Regex ImportStarPattern = new(@"^\s*from\s+(\S+)\s+import\s+\*", RegexOptions.Compiled | RegexOptions.Multiline);
    public static readonly Regex CSharpCommentPattern = new(
        @"//.*?$|/\*.*?\*/",
        RegexOptions.Compiled | RegexOptions.Multiline);
    public static readonly Regex CSharpMethodSignaturePattern = new(
        @"^[ \t]*(?:(?:public|private|protected|internal|static|virtual|override|abstract|sealed|async|new|partial|readonly)\s+)*(?!return\b|if\b|else\b|while\b|for\b|foreach\b|switch\b|catch\b|using\b|lock\b|yield\b|class\b|struct\b|interface\b|enum\b|record\b|namespace\b|delegate\b)[\w<>\[\],\?]+\s+(\w+)\s*(?:<[^>]+>)?\s*\(",
        RegexOptions.Compiled | RegexOptions.Multiline);
    public static readonly Regex PythonDefPattern = new(@"^[ \t]*def\s+(\w+)\s*\(", RegexOptions.Compiled | RegexOptions.Multiline);
    public static readonly Regex CppFunctionPattern = new(
        @"^[ \t]*(?:(?:static|virtual|inline|explicit|constexpr|const|unsigned|signed|volatile|extern|friend|template\s*<[^>]*>)\s+)*[\w:*&<>,\s]+\s+(\w+)\s*\([^;]*$",
        RegexOptions.Compiled | RegexOptions.Multiline);
    public static readonly Regex PoorCSharpNamingPattern = new(
        @"\b(?:var|int|string|bool|double|float|long|object|dynamic)\s+(?<name>tmp|temp|data|val|res|ret|result|process|handle|flag|item|stuff|thing|manager|helper|util|misc)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    public static readonly Regex SingleLetterVarPattern = new(
        @"(?:(?:var|int|string|bool|double|float|long|short|byte|char|object|decimal)\s+(?<name>[a-zA-Z])\s*[=;,)])",
        RegexOptions.Compiled);
    public static readonly Regex ImplicitVarPattern = new(
        @"(?<!\.)\bvar\s+(?<name>@?[A-Za-z_][A-Za-z0-9_]*)\s*(?:=|;|,)",
        RegexOptions.Compiled);
    public static readonly Regex BroadCatchPattern = new(
        @"catch\s*\(\s*(?:System\.)?Exception(?:\s+[A-Za-z_][A-Za-z0-9_]*)?\s*\)",
        RegexOptions.Compiled);
    public static readonly Regex FrameworkTypePattern = new(
        @"\bSystem\.(?<type>String|Int16|Int32|Int64|UInt16|UInt32|UInt64|Boolean|Object|Decimal|Double|Single|Byte|SByte|Char)\b",
        RegexOptions.Compiled);
    public static readonly Regex VbOptionStrictOnPattern = new(
        @"(?im)^\s*Option\s+Strict\s+On\b",
        RegexOptions.Compiled);
    public static readonly Regex VbOptionStrictOffPattern = new(
        @"(?im)^\s*Option\s+Strict\s+Off\b",
        RegexOptions.Compiled);
    public static readonly Regex VbLineContinuationPattern = new(
        @"(?m)_\s*(?:'.*)?$",
        RegexOptions.Compiled);
    public static readonly Regex FSharpMutablePattern = new(
        @"\bmutable\b",
        RegexOptions.Compiled);
    public static readonly Regex FSharpBlockCommentPattern = new(
        @"\(\*.*?\*\)",
        RegexOptions.Compiled | RegexOptions.Singleline);
    public static readonly Regex PythonNoneComparisonPattern = new(
        @"(?:==|!=)\s*None\b|\bNone\s*(?:==|!=)",
        RegexOptions.Compiled);
    public static readonly Regex PowerShellAliasPattern = new(
        @"(?im)(?:^|[|;]\s*)(?<alias>ls|dir|gc|cat|echo|sleep|cp|mv|rm|del|%|\?)\b",
        RegexOptions.Compiled);
    public static readonly Regex PythonPoorNamingPattern = new(
        @"^[ \t]*(?<name>tmp|temp|data|val|res|ret|result|process|handle|flag|stuff|thing)\s*=",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);
    public static readonly Regex PythonSingleLetterAssignPattern = new(
        @"^[ \t]*(?<name>[a-zA-Z])\s*=\s*(?!.*\bfor\b)",
        RegexOptions.Compiled | RegexOptions.Multiline);
    public static readonly Regex CSharpCommentedCodePattern = new(
        @"^\s*//\s*(?:(?:public|private|protected|internal|static|var|if|else|for|foreach|while|return|throw|try|catch|class|using|namespace|void|int|string|bool)\b|\w+\s*\(.*\)\s*[;{]|\w+\s*=\s*)",
        RegexOptions.Compiled);
    public static readonly Regex PythonCommentedCodePattern = new(
        @"^\s*#\s*(?:(?:def|class|if|else|elif|for|while|return|import|from|try|except|raise|with|yield)\b|\w+\s*\(.*\)\s*$|\w+\s*=\s*)",
        RegexOptions.Compiled);
    public static readonly Regex CppCommentedCodePattern = new(
        @"^\s*//\s*(?:(?:class|struct|if|else|for|while|return|throw|try|catch|namespace|void|int|auto|const|static|virtual|template)\b|\w+\s*\(.*\)\s*[;{]|\w+\s*=\s*)",
        RegexOptions.Compiled);
    public static readonly Regex TabIndentedLinePattern = new(@"^\t", RegexOptions.Compiled | RegexOptions.Multiline);
    public static readonly Regex SpaceIndentedLinePattern = new(@"^ {2,}", RegexOptions.Compiled | RegexOptions.Multiline);
    public static readonly Regex CSharpClassDeclPattern = new(
        @"^[ \t]*(?:(?:public|private|protected|internal|static|sealed|abstract|partial)\s+)*class\s+(\w+)",
        RegexOptions.Compiled | RegexOptions.Multiline);
    public static readonly Regex CSharpFieldDeclPattern = new(
        @"^[ \t]*(?:(?:public|private|protected|internal|static|readonly|volatile|const)\s+)+[\w<>\[\],\?\s]+\s+_?\w+\s*[=;]",
        RegexOptions.Compiled | RegexOptions.Multiline);
    public static readonly Regex NewDisposablePattern = new(
        @"(?<!using\s*\([^)]*)\b(?:var|[\w<>\[\]]+)\s+(\w+)\s*=\s*new\s+(?:Stream(?:Reader|Writer)|FileStream|Http(?:Client|ResponseMessage)|SqlConnection|SqlCommand|Process|Timer|MemoryStream|BinaryReader|BinaryWriter|WebClient|TcpClient|UdpClient|NetworkStream|CryptoStream)\s*\(",
        RegexOptions.Compiled);
    public static readonly Regex DateTimeInLoopPattern = new(
        @"(?:for\s*\(|foreach\s*\(|while\s*\()[^{]*\{[^}]*DateTime\s*\.\s*(?:Now|UtcNow)",
        RegexOptions.Compiled | RegexOptions.Singleline);
    public static readonly Regex DateTimeNowSimplePattern = new(
        @"DateTime\s*\.\s*(?<prop>Now|UtcNow)",
        RegexOptions.Compiled);
    public static readonly Regex DynamicObjectParamPattern = new(
        @"\b(?:dynamic|object)\s+\w+\s*[,)]",
        RegexOptions.Compiled);
    public static readonly Regex RawNewPattern = new(
        @"(?<!(?:unique_ptr|shared_ptr|make_unique|make_shared|reset|emplace)\s*(?:<[^>]*>\s*)?\()\bnew\s+\w+",
        RegexOptions.Compiled);
    public static readonly Regex PreprocessorDefinePattern = new(
        @"^\s*#\s*define\s+(\w+)",
        RegexOptions.Compiled | RegexOptions.Multiline);
    public static readonly Regex CppNonConstMethodPattern = new(
        @"^\s*(?:virtual\s+)?(?:[\w:*&<>,\s]+)\s+(\w+)\s*\([^)]*\)\s*(?:override\s*)?(?=\s*\{)",
        RegexOptions.Compiled | RegexOptions.Multiline);
    public static readonly Regex PythonBoolComparePattern = new(
        @"(?:==\s*True|==\s*False|is\s+True|is\s+False)\b",
        RegexOptions.Compiled);
    public static readonly Regex CSharpAutoPropertyPattern = new(
        @"^[ \t]*(?:(?:public|private|protected|internal|static|virtual|override|required|sealed|abstract)\s+)+[\w<>\[\],\?\s]+\s+\w+\s*\{\s*get;\s*(?:set;|init;)?\s*\}",
        RegexOptions.Compiled | RegexOptions.Multiline);
    public static readonly Regex CSharpNamespacePattern = new(
        @"^\s*namespace\s+(?<name>[A-Za-z_][A-Za-z0-9_.]*)\s*(?:;|\{)",
        RegexOptions.Compiled | RegexOptions.Multiline);
    public static readonly Regex PartialTypeDeclarationPattern = new(
        @"\bpartial\s+(?:class|struct|interface|record)\s+(?<name>@?[A-Za-z_][A-Za-z0-9_]*)\b",
        RegexOptions.Compiled);
}