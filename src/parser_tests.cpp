#define CODEXBAR_PARSER_TESTS
#include "main.cpp"

#include <iostream>

int main() {
    int failures = 0;
    auto expect = [&](bool condition, const char* name) {
        if (!condition) {
            std::cerr << "FAILED: " << name << "\n";
            failures++;
        }
    };

    expect(DecodeJsonString("line\\nnext") == "line\nnext", "DecodeJsonString newline");
    expect(DecodeJsonString("\\u0041") == "A", "DecodeJsonString BMP unicode");
    expect(DecodeJsonString("\\uD83D\\uDE00") == std::string("\xF0\x9F\x98\x80", 4), "DecodeJsonString surrogate pair");

    std::string escaped = "{\"message\":\"hello \\\"quoted\\\"\"}";
    expect(RegexString(escaped, "message") == "hello \"quoted\"", "RegexString escaped quote");
    expect(RegexInt("{\"value\":2147483648}", "value", -7) == -7, "RegexInt overflow fallback");
    expect(RegexBool("{\"ok\":true}", "ok", false), "RegexBool true");
    bool parsedBool = false;
    expect(TryRegexBool("{\"ok\":false}", "ok", parsedBool) && !parsedBool, "TryRegexBool false");
    expect(!TryRegexBool("{\"ok\":}", "ok", parsedBool), "TryRegexBool missing value");

    expect(IsJsonRpcResponseLine("{\"id\":3,\"result\":{\"ok\":true}}", 3), "JSON-RPC result line");
    expect(IsJsonRpcResponseLine("{\"id\":3,\"error\":{\"message\":\"nope\"}}", 3), "JSON-RPC error line");
    expect(!IsJsonRpcResponseLine("{\"id\":3,\"method\":\"progress\"}", 3), "JSON-RPC ignores notification with id");
    expect(!IsJsonRpcResponseLine("{\"method\":\"progress\",\"params\":{\"id\":3,\"text\":\"result\"}}", 3), "JSON-RPC ignores nested id");
    expect(TopLevelJsonInt("{\"params\":{\"id\":9},\"id\":4,\"result\":{}}", "id", -1) == 4, "TopLevelJsonInt envelope id");
    expect(CompleteLinesOnly("one\ntwo") == "one\n", "CompleteLinesOnly drops partial");
    expect(CompleteLinesOnly("one\ntwo\n") == "one\ntwo\n", "CompleteLinesOnly keeps final newline");

    std::string quota = "{\"id\":3,\"result\":{\"secondary\":{\"usedPercent\":88},\"primary\":{\"label\":\"brace } in string\",\"usedPercent\":12}}}";
    expect(RegexInt(ExtractObjectForKey(quota, "primary"), "usedPercent", -1) == 12, "ExtractObjectForKey primary");
    expect(RegexInt(ExtractObjectForKey(quota, "secondary"), "usedPercent", -1) == 88, "ExtractObjectForKey secondary");

    std::vector<std::string> objects = ExtractObjectsInArray("{\"items\":[{\"a\":\"}\"},{\"a\":2}]}", "items");
    expect(objects.size() == 2, "ExtractObjectsInArray escaped brace");

    expect(IsSafeLoginUrl(L"https://auth.openai.com/oauth"), "IsSafeLoginUrl https");
    expect(IsSafeLoginUrl(L"http://localhost:1455/auth/callback"), "IsSafeLoginUrl localhost");
    expect(!IsSafeLoginUrl(L"http://localhost.evil.test/auth"), "IsSafeLoginUrl localhost boundary");
    expect(!IsSafeLoginUrl(L"file:///C:/Windows/notepad.exe"), "IsSafeLoginUrl rejects file");

    if (failures == 0) {
        std::cout << "parser tests passed\n";
    }
    return failures == 0 ? 0 : 1;
}
