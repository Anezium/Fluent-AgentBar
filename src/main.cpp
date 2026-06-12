#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif

#include <windows.h>
#include <windowsx.h>
#include <shellapi.h>
#include <shlobj.h>

#include "resource.h"

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cctype>
#include <cwctype>
#include <cstring>
#include <cstdio>
#include <deque>
#include <map>
#include <mutex>
#include <limits>
#include <optional>
#include <regex>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

#ifndef WM_DPICHANGED
#define WM_DPICHANGED 0x02E0
#endif

#ifndef DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
#define DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 reinterpret_cast<HANDLE>(-4)
#endif

namespace {

constexpr UINT WM_TRAYICON = WM_APP + 1;
constexpr UINT WM_REFRESH_DONE = WM_APP + 2;
constexpr UINT WM_LOGIN_DONE = WM_APP + 3;
constexpr UINT WM_LOGIN_OPEN_URL = WM_APP + 4;
constexpr UINT WM_REFRESH_REQUEST = WM_APP + 5;
constexpr UINT WM_SHOW_SETTINGS = WM_APP + 6;
constexpr UINT TIMER_REFRESH = 101;
constexpr UINT TIMER_TASKBAR_REPOSITION = 102;
constexpr UINT TIMER_FLYOUT_WATCHDOG = 103;
constexpr UINT TRAY_ID = 42;
constexpr DWORD FLYOUT_AUTO_HIDE_MS = 12000;
constexpr DWORD FLYOUT_OUTSIDE_CLICK_GRACE_MS = 350;

constexpr int MENU_REFRESH = 1001;
constexpr int MENU_OPEN_CONFIG = 1002;
constexpr int MENU_OPEN_PROFILES = 1003;
constexpr int MENU_EXIT = 1004;
constexpr int MENU_EDIT_CONFIG = 1005;
constexpr int MENU_ADD_PROFILE = 1006;
constexpr int MENU_LOGIN_PROFILE_BASE = 1100;
constexpr int MENU_LOGIN_PROFILE_LIMIT = 1199;

const wchar_t* kMainClass = L"CodexSWBarWindows.Main";
const wchar_t* kTaskbarPresenceClass = L"CodexSWBarWindows.TaskbarPresence";
const wchar_t* kCodexBarFlyoutClass = L"CodexSWBarWindows.CodexBarFlyout";
const wchar_t* kContextMenuClass = L"CodexSWBarWindows.ContextMenu";

constexpr wchar_t kGlyphRefresh[] = {0xE72C, 0};
constexpr wchar_t kGlyphSettings[] = {0xE713, 0};
constexpr wchar_t kGlyphDocument[] = {0xE8A5, 0};
constexpr wchar_t kGlyphChevronRight[] = {0xE76C, 0};
constexpr wchar_t kGlyphColor[] = {0xE790, 0};
constexpr wchar_t kGlyphChat[] = {0xE8F2, 0};
constexpr wchar_t kGlyphFolder[] = {0xE8B7, 0};
constexpr wchar_t kGlyphPerson[] = {0xE77B, 0};
constexpr wchar_t kGlyphClock[] = {0xE823, 0};
constexpr wchar_t kGlyphAdd[] = {0xE710, 0};
constexpr wchar_t kGlyphPower[] = {0xE7E8, 0};
const wchar_t* kSettingsClass = L"CodexSWBarWindows.Settings";
const wchar_t* kTextPromptClass = L"CodexSWBarWindows.TextPrompt";
const wchar_t* kAppTitle = L"Codex SWBar Windows";
UINT g_taskbarCreatedMessage = 0;
HANDLE g_singleInstanceMutex = nullptr;
std::mutex g_childProcessInheritanceMutex;

enum class UiAction {
    Refresh,
    LoginProfile,
    RenameProfile,
    ToggleProfile,
    OpenProfileFolder,
    AddProfile,
    EditConfig,
    OpenConfig,
    OpenProfiles,
    Exit
};

struct HitTarget {
    RECT rect{};
    UiAction action = UiAction::Refresh;
    int profileIndex = -1;
};

struct TargetKey {
    bool valid = false;
    UiAction action = UiAction::Refresh;
    int profileIndex = -1;
};

std::wstring Utf8ToWide(const std::string& s) {
    if (s.empty()) return L"";
    int len = MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), nullptr, 0);
    std::wstring out(len, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), out.data(), len);
    return out;
}

std::string WideToUtf8(const std::wstring& s) {
    if (s.empty()) return "";
    int len = WideCharToMultiByte(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), nullptr, 0, nullptr, nullptr);
    std::string out(len, '\0');
    WideCharToMultiByte(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), out.data(), len, nullptr, nullptr);
    return out;
}

std::wstring Trim(const std::wstring& value) {
    size_t start = 0;
    while (start < value.size() && iswspace(value[start])) start++;
    size_t end = value.size();
    while (end > start && iswspace(value[end - 1])) end--;
    return value.substr(start, end - start);
}

std::string TrimUtf8(const std::string& value) {
    size_t start = 0;
    while (start < value.size() && std::isspace(static_cast<unsigned char>(value[start]))) start++;
    size_t end = value.size();
    while (end > start && std::isspace(static_cast<unsigned char>(value[end - 1]))) end--;
    return value.substr(start, end - start);
}

bool FileExists(const std::wstring& path) {
    DWORD attrs = GetFileAttributesW(path.c_str());
    return attrs != INVALID_FILE_ATTRIBUTES && !(attrs & FILE_ATTRIBUTE_DIRECTORY);
}

bool DirectoryExists(const std::wstring& path) {
    DWORD attrs = GetFileAttributesW(path.c_str());
    return attrs != INVALID_FILE_ATTRIBUTES && (attrs & FILE_ATTRIBUTE_DIRECTORY);
}

void EnsureDirectory(const std::wstring& path) {
    if (path.empty() || DirectoryExists(path)) return;
    SHCreateDirectoryExW(nullptr, path.c_str(), nullptr);
}

std::wstring GetEnvVar(const wchar_t* name) {
    DWORD needed = GetEnvironmentVariableW(name, nullptr, 0);
    if (needed == 0) return L"";
    std::wstring value(needed, L'\0');
    DWORD written = GetEnvironmentVariableW(name, value.data(), needed);
    value.resize(written);
    return value;
}

std::wstring ExpandEnv(const std::wstring& input) {
    DWORD needed = ExpandEnvironmentStringsW(input.c_str(), nullptr, 0);
    if (needed == 0) return input;
    std::wstring out(needed, L'\0');
    DWORD written = ExpandEnvironmentStringsW(input.c_str(), out.data(), needed);
    if (written == 0) return input;
    out.resize(written - 1);
    return out;
}

std::wstring ParentDir(const std::wstring& path) {
    size_t slash = path.find_last_of(L"\\/");
    if (slash == std::wstring::npos) return L"";
    return path.substr(0, slash);
}

std::string ReadTextFileUtf8(const std::wstring& path) {
    HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return "";

    std::string data;
    char buffer[4096];
    DWORD read = 0;
    while (ReadFile(file, buffer, sizeof(buffer), &read, nullptr) && read > 0) {
        data.append(buffer, buffer + read);
    }
    CloseHandle(file);
    return data;
}

bool WriteTextFileUtf8(const std::wstring& path, const std::string& content) {
    EnsureDirectory(ParentDir(path));
    HANDLE file = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;
    DWORD written = 0;
    BOOL ok = WriteFile(file, content.data(), static_cast<DWORD>(content.size()), &written, nullptr);
    CloseHandle(file);
    return ok && written == content.size();
}

std::wstring QuoteForCmd(const std::wstring& value) {
    std::wstring out = L"\"";
    for (wchar_t ch : value) {
        if (ch == L'"') out += L"\\\"";
        else out += ch;
    }
    out += L"\"";
    return out;
}

std::wstring SystemCmdPath() {
    wchar_t buffer[MAX_PATH]{};
    UINT length = GetSystemDirectoryW(buffer, ARRAYSIZE(buffer));
    if (length == 0 || length >= ARRAYSIZE(buffer)) return L"C:\\Windows\\System32\\cmd.exe";
    return std::wstring(buffer, length) + L"\\cmd.exe";
}

std::wstring ShellCommand(const std::wstring& command) {
    return QuoteForCmd(SystemCmdPath()) + L" /D /S /C " + QuoteForCmd(command);
}

struct CaseInsensitiveLess {
    bool operator()(const std::wstring& left, const std::wstring& right) const {
        return _wcsicmp(left.c_str(), right.c_str()) < 0;
    }
};

std::wstring BuildEnvironmentBlock(const std::map<std::wstring, std::wstring, CaseInsensitiveLess>& overrides) {
    std::map<std::wstring, std::wstring, CaseInsensitiveLess> entries;
    LPWCH env = GetEnvironmentStringsW();
    if (env) {
        for (LPWCH current = env; *current; current += wcslen(current) + 1) {
            std::wstring entry = current;
            size_t searchStart = (!entry.empty() && entry[0] == L'=') ? 1 : 0;
            size_t equals = entry.find(L'=', searchStart);
            if (equals == std::wstring::npos) continue;
            std::wstring key = entry.substr(0, equals);
            std::wstring value = entry.substr(equals + 1);
            entries[key] = value;
        }
        FreeEnvironmentStringsW(env);
    }

    for (const auto& [key, value] : overrides) {
        entries[key] = value;
    }

    std::wstring block;
    for (const auto& [key, value] : entries) {
        block += key;
        block += L'=';
        block += value;
        block += L'\0';
    }
    block += L'\0';
    return block;
}

void CloseHandleIfOpen(HANDLE& handle) {
    if (handle) {
        CloseHandle(handle);
        handle = nullptr;
    }
}

HANDLE CreateKillOnCloseJob() {
    HANDLE job = CreateJobObjectW(nullptr, nullptr);
    if (!job) return nullptr;

    JOBOBJECT_EXTENDED_LIMIT_INFORMATION info{};
    info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, &info, sizeof(info))) {
        CloseHandle(job);
        return nullptr;
    }
    return job;
}

void AssignToKillJob(HANDLE& job, HANDLE process) {
    job = CreateKillOnCloseJob();
    if (job && !AssignProcessToJobObject(job, process)) {
        CloseHandleIfOpen(job);
    }
}

void TerminateProcessTree(HANDLE& job, HANDLE process) {
    if (job) {
        TerminateJobObject(job, 1);
        CloseHandleIfOpen(job);
    } else {
        TerminateProcess(process, 1);
    }
}

std::string DecodeJsonString(const std::string& value) {
    std::string out;
    auto hexValue = [](char ch) -> int {
        if (ch >= '0' && ch <= '9') return ch - '0';
        if (ch >= 'a' && ch <= 'f') return 10 + (ch - 'a');
        if (ch >= 'A' && ch <= 'F') return 10 + (ch - 'A');
        return -1;
    };
    auto readHex4 = [&](size_t pos, unsigned int& code) -> bool {
        if (pos + 4 > value.size()) return false;
        code = 0;
        for (size_t j = 0; j < 4; ++j) {
            int digit = hexValue(value[pos + j]);
            if (digit < 0) return false;
            code = (code << 4) | static_cast<unsigned int>(digit);
        }
        return true;
    };
    auto appendUtf8 = [&](unsigned int codePoint) {
        if (codePoint <= 0x7F) {
            out += static_cast<char>(codePoint);
        } else if (codePoint <= 0x7FF) {
            out += static_cast<char>(0xC0 | ((codePoint >> 6) & 0x1F));
            out += static_cast<char>(0x80 | (codePoint & 0x3F));
        } else if (codePoint <= 0xFFFF) {
            out += static_cast<char>(0xE0 | ((codePoint >> 12) & 0x0F));
            out += static_cast<char>(0x80 | ((codePoint >> 6) & 0x3F));
            out += static_cast<char>(0x80 | (codePoint & 0x3F));
        } else if (codePoint <= 0x10FFFF) {
            out += static_cast<char>(0xF0 | ((codePoint >> 18) & 0x07));
            out += static_cast<char>(0x80 | ((codePoint >> 12) & 0x3F));
            out += static_cast<char>(0x80 | ((codePoint >> 6) & 0x3F));
            out += static_cast<char>(0x80 | (codePoint & 0x3F));
        }
    };

    for (size_t i = 0; i < value.size(); ++i) {
        char ch = value[i];
        if (ch != '\\' || i + 1 >= value.size()) {
            out += ch;
            continue;
        }
        char next = value[++i];
        switch (next) {
            case '"': out += '"'; break;
            case '\\': out += '\\'; break;
            case '/': out += '/'; break;
            case 'b': out += '\b'; break;
            case 'f': out += '\f'; break;
            case 'n': out += '\n'; break;
            case 'r': out += '\r'; break;
            case 't': out += '\t'; break;
            case 'u': {
                unsigned int code = 0;
                if (!readHex4(i + 1, code)) {
                    out += 'u';
                    break;
                }
                i += 4;
                if (code >= 0xD800 && code <= 0xDBFF && i + 6 < value.size() && value[i + 1] == '\\' && value[i + 2] == 'u') {
                    unsigned int low = 0;
                    if (readHex4(i + 3, low) && low >= 0xDC00 && low <= 0xDFFF) {
                        code = 0x10000 + (((code - 0xD800) << 10) | (low - 0xDC00));
                        i += 6;
                    }
                }
                appendUtf8(code);
                break;
            }
            default: out += next; break;
        }
    }
    return out;
}

std::string RegexString(const std::string& text, const std::string& field) {
    std::regex pattern("\"" + field + "\"\\s*:\\s*\"((?:\\\\.|[^\"])*)\"");
    std::smatch match;
    if (std::regex_search(text, match, pattern)) return DecodeJsonString(match[1].str());
    return "";
}

bool RegexBool(const std::string& text, const std::string& field, bool fallback) {
    std::regex pattern("\"" + field + "\"\\s*:\\s*(true|false)");
    std::smatch match;
    if (std::regex_search(text, match, pattern)) return match[1].str() == "true";
    return fallback;
}

bool TryRegexBool(const std::string& text, const std::string& field, bool& value) {
    std::regex pattern("\"" + field + "\"\\s*:\\s*(true|false)");
    std::smatch match;
    if (!std::regex_search(text, match, pattern)) return false;
    value = match[1].str() == "true";
    return true;
}

int RegexInt(const std::string& text, const std::string& field, int fallback) {
    std::regex pattern("\"" + field + "\"\\s*:\\s*(-?\\d+)");
    std::smatch match;
    if (std::regex_search(text, match, pattern)) {
        try {
            long long value = std::stoll(match[1].str());
            if (value < std::numeric_limits<int>::min() || value > std::numeric_limits<int>::max()) {
                return fallback;
            }
            return static_cast<int>(value);
        } catch (...) {
            return fallback;
        }
    }
    return fallback;
}

size_t FindTopLevelFieldValue(const std::string& json, const std::string& field) {
    std::string needle = "\"" + field + "\"";
    bool inString = false;
    bool escape = false;
    int depth = 0;
    for (size_t i = 0; i < json.size(); ++i) {
        char ch = json[i];
        if (inString) {
            if (escape) {
                escape = false;
            } else if (ch == '\\') {
                escape = true;
            } else if (ch == '"') {
                inString = false;
            }
            continue;
        }

        if (depth == 1 && ch == '"' && json.compare(i, needle.size(), needle) == 0) {
            size_t pos = i + needle.size();
            while (pos < json.size() && std::isspace(static_cast<unsigned char>(json[pos]))) pos++;
            if (pos < json.size() && json[pos] == ':') return pos + 1;
        }

        if (ch == '"') {
            inString = true;
        } else if (ch == '{') {
            depth++;
        } else if (ch == '}') {
            depth--;
        }
    }
    return std::string::npos;
}

int TopLevelJsonInt(const std::string& json, const std::string& field, int fallback) {
    size_t pos = FindTopLevelFieldValue(json, field);
    if (pos == std::string::npos) return fallback;
    while (pos < json.size() && std::isspace(static_cast<unsigned char>(json[pos]))) pos++;
    size_t start = pos;
    if (pos < json.size() && json[pos] == '-') pos++;
    while (pos < json.size() && std::isdigit(static_cast<unsigned char>(json[pos]))) pos++;
    if (pos == start || (pos == start + 1 && json[start] == '-')) return fallback;
    try {
        long long value = std::stoll(json.substr(start, pos - start));
        if (value < std::numeric_limits<int>::min() || value > std::numeric_limits<int>::max()) {
            return fallback;
        }
        return static_cast<int>(value);
    } catch (...) {
        return fallback;
    }
}

bool HasTopLevelField(const std::string& json, const std::string& field) {
    return FindTopLevelFieldValue(json, field) != std::string::npos;
}

bool IsJsonRpcResponseLine(const std::string& line, int responseId) {
    return TopLevelJsonInt(line, "id", -1) == responseId &&
           (HasTopLevelField(line, "result") || HasTopLevelField(line, "error"));
}

std::string CompleteLinesOnly(const std::string& text) {
    if (text.empty() || text.back() == '\n' || text.back() == '\r') return text;
    size_t lastBreak = text.find_last_of("\r\n");
    if (lastBreak == std::string::npos) return "";
    return text.substr(0, lastBreak + 1);
}

std::vector<std::string> ExtractObjectsInArray(const std::string& json, const std::string& arrayName) {
    std::vector<std::string> objects;
    std::string needle = "\"" + arrayName + "\"";
    size_t pos = json.find(needle);
    if (pos == std::string::npos) return objects;
    pos = json.find('[', pos);
    if (pos == std::string::npos) return objects;

    bool inString = false;
    bool escape = false;
    int depth = 0;
    size_t objectStart = std::string::npos;
    for (size_t i = pos + 1; i < json.size(); ++i) {
        char ch = json[i];
        if (inString) {
            if (escape) {
                escape = false;
            } else if (ch == '\\') {
                escape = true;
            } else if (ch == '"') {
                inString = false;
            }
            continue;
        }
        if (ch == '"') {
            inString = true;
            continue;
        }
        if (ch == ']') {
            if (depth == 0) break;
        } else if (ch == '{') {
            if (depth == 0) objectStart = i;
            depth++;
        } else if (ch == '}') {
            depth--;
            if (depth == 0 && objectStart != std::string::npos) {
                objects.push_back(json.substr(objectStart, i - objectStart + 1));
                objectStart = std::string::npos;
            }
        }
    }
    return objects;
}

std::string ExtractObjectForKey(const std::string& json, const std::string& key) {
    std::string needle = "\"" + key + "\"";
    size_t search = 0;
    while (true) {
        size_t keyPos = json.find(needle, search);
        if (keyPos == std::string::npos) return "";
        size_t colon = json.find(':', keyPos + needle.size());
        if (colon == std::string::npos) return "";
        size_t objectStart = json.find_first_not_of(" \t\r\n", colon + 1);
        if (objectStart == std::string::npos) return "";
        if (json[objectStart] != '{') {
            search = objectStart + 1;
            continue;
        }

        bool inString = false;
        bool escape = false;
        int depth = 0;
        for (size_t i = objectStart; i < json.size(); ++i) {
            char ch = json[i];
            if (inString) {
                if (escape) {
                    escape = false;
                } else if (ch == '\\') {
                    escape = true;
                } else if (ch == '"') {
                    inString = false;
                }
                continue;
            }
            if (ch == '"') {
                inString = true;
            } else if (ch == '{') {
                depth++;
            } else if (ch == '}') {
                depth--;
                if (depth == 0) return json.substr(objectStart, i - objectStart + 1);
            }
        }
        return "";
    }
}

struct ProcessResult {
    DWORD exitCode = 0;
    bool timedOut = false;
    std::string stdoutText;
    std::string stderrText;
};

ProcessResult RunProcess(
    const std::wstring& commandLine,
    const std::string& stdinText,
    DWORD timeoutMs,
    const std::string& closeStdinAfterStdoutContains = "",
    DWORD maxStdinOpenMs = 0,
    const std::map<std::wstring, std::wstring, CaseInsensitiveLess>& envOverrides = {},
    int closeStdinAfterResponseId = -1,
    const std::atomic_bool* cancelFlag = nullptr
) {
    ProcessResult result;

    SECURITY_ATTRIBUTES sa{};
    sa.nLength = sizeof(sa);
    sa.bInheritHandle = TRUE;

    HANDLE stdoutRead = nullptr, stdoutWrite = nullptr;
    HANDLE stderrRead = nullptr, stderrWrite = nullptr;
    HANDLE stdinRead = nullptr, stdinWrite = nullptr;
    HANDLE job = nullptr;
    std::unique_lock<std::mutex> childCreationLock(g_childProcessInheritanceMutex);

    if (!CreatePipe(&stdoutRead, &stdoutWrite, &sa, 0)) return result;
    if (!CreatePipe(&stderrRead, &stderrWrite, &sa, 0)) {
        CloseHandleIfOpen(stdoutRead);
        CloseHandleIfOpen(stdoutWrite);
        return result;
    }
    if (!CreatePipe(&stdinRead, &stdinWrite, &sa, 0)) {
        CloseHandleIfOpen(stdoutRead);
        CloseHandleIfOpen(stdoutWrite);
        CloseHandleIfOpen(stderrRead);
        CloseHandleIfOpen(stderrWrite);
        return result;
    }

    SetHandleInformation(stdoutRead, HANDLE_FLAG_INHERIT, 0);
    SetHandleInformation(stderrRead, HANDLE_FLAG_INHERIT, 0);
    SetHandleInformation(stdinWrite, HANDLE_FLAG_INHERIT, 0);

    STARTUPINFOW si{};
    si.cb = sizeof(si);
    si.dwFlags = STARTF_USESTDHANDLES | STARTF_USESHOWWINDOW;
    si.wShowWindow = SW_HIDE;
    si.hStdOutput = stdoutWrite;
    si.hStdError = stderrWrite;
    si.hStdInput = stdinRead;

    PROCESS_INFORMATION pi{};
    std::wstring mutableCommand = commandLine;
    std::wstring envBlock = envOverrides.empty() ? L"" : BuildEnvironmentBlock(envOverrides);
    BOOL ok = CreateProcessW(
        nullptr,
        mutableCommand.data(),
        nullptr,
        nullptr,
        TRUE,
        CREATE_NO_WINDOW | CREATE_SUSPENDED | (envOverrides.empty() ? 0 : CREATE_UNICODE_ENVIRONMENT),
        envOverrides.empty() ? nullptr : envBlock.data(),
        nullptr,
        &si,
        &pi
    );
    DWORD processError = ok ? ERROR_SUCCESS : GetLastError();

    CloseHandle(stdoutWrite);
    CloseHandle(stderrWrite);
    CloseHandle(stdinRead);

    if (ok) {
        AssignToKillJob(job, pi.hProcess);
        if (!job) {
            processError = ERROR_ACCESS_DENIED;
            TerminateProcess(pi.hProcess, 1);
            ok = FALSE;
            SetLastError(ERROR_ACCESS_DENIED);
        } else if (ResumeThread(pi.hThread) == static_cast<DWORD>(-1)) {
            processError = GetLastError();
            TerminateProcessTree(job, pi.hProcess);
            ok = FALSE;
        }
    }

    if (!ok) {
        CloseHandle(stdoutRead);
        CloseHandle(stderrRead);
        CloseHandle(stdinWrite);
        CloseHandleIfOpen(job);
        if (pi.hThread) CloseHandle(pi.hThread);
        if (pi.hProcess) CloseHandle(pi.hProcess);
        result.exitCode = processError;
        return result;
    }
    childCreationLock.unlock();

    std::mutex outputMutex;
    std::string stdoutText;
    std::string stderrText;

    auto readToString = [&](HANDLE handle, std::string& target) {
        char buffer[4096];
        DWORD read = 0;
        while (ReadFile(handle, buffer, sizeof(buffer), &read, nullptr) && read > 0) {
            std::lock_guard<std::mutex> lock(outputMutex);
            target.append(buffer, buffer + read);
        }
    };

    std::thread stdoutThread([&] { readToString(stdoutRead, stdoutText); });
    std::thread stderrThread([&] { readToString(stderrRead, stderrText); });

    if (!stdinText.empty()) {
        DWORD written = 0;
        WriteFile(stdinWrite, stdinText.data(), static_cast<DWORD>(stdinText.size()), &written, nullptr);
    }

    if (maxStdinOpenMs > 0) {
        auto start = std::chrono::steady_clock::now();
        while ((!cancelFlag || !cancelFlag->load()) &&
               std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - start).count() < maxStdinOpenMs) {
            if (WaitForSingleObject(pi.hProcess, 0) != WAIT_TIMEOUT) break;
            if (!closeStdinAfterStdoutContains.empty()) {
                std::lock_guard<std::mutex> lock(outputMutex);
                if (stdoutText.find(closeStdinAfterStdoutContains) != std::string::npos) break;
            }
            if (closeStdinAfterResponseId >= 0) {
                std::lock_guard<std::mutex> lock(outputMutex);
                bool found = false;
                std::istringstream stream(CompleteLinesOnly(stdoutText));
                std::string line;
                while (std::getline(stream, line)) {
                    if (IsJsonRpcResponseLine(line, closeStdinAfterResponseId)) {
                        found = true;
                        break;
                    }
                }
                if (found) break;
            }
            Sleep(100);
        }
    }
    CloseHandle(stdinWrite);

    auto waitStart = std::chrono::steady_clock::now();
    DWORD wait = WAIT_TIMEOUT;
    while (true) {
        wait = WaitForSingleObject(pi.hProcess, 100);
        if (wait != WAIT_TIMEOUT) break;
        if (cancelFlag && cancelFlag->load()) break;
        if (std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - waitStart).count() >= timeoutMs) break;
    }
    if (wait == WAIT_TIMEOUT) {
        result.timedOut = true;
        bool cancelled = cancelFlag && cancelFlag->load();
        TerminateProcessTree(job, pi.hProcess);
        WaitForSingleObject(pi.hProcess, cancelled ? 300 : 1000);
    }

    GetExitCodeProcess(pi.hProcess, &result.exitCode);
    CloseHandleIfOpen(job);
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);

    if (stdoutThread.joinable()) stdoutThread.join();
    if (stderrThread.joinable()) stderrThread.join();
    CloseHandle(stdoutRead);
    CloseHandle(stderrRead);

    {
        std::lock_guard<std::mutex> lock(outputMutex);
        result.stdoutText = stdoutText;
        result.stderrText = stderrText;
    }

    return result;
}

struct CodexProfileConfig {
    std::wstring label;
    std::wstring codexHome;
    bool enabled = true;
};

struct ClaudeConfig {
    bool enabled = true;
};

struct AppConfig {
    int refreshIntervalSeconds = 300;
    std::wstring flyoutStyle = L"acrylic";
    std::vector<CodexProfileConfig> codexProfiles;
    ClaudeConfig claude;
};

std::wstring ConfigDir() {
    std::wstring appData = GetEnvVar(L"APPDATA");
    if (appData.empty()) appData = GetEnvVar(L"USERPROFILE");
    return appData + L"\\Codex SWBar Windows";
}

std::wstring ConfigPath() {
    return ConfigDir() + L"\\config.json";
}

std::wstring DefaultCodexProfileHome() {
    return ConfigDir() + L"\\profiles\\main";
}

std::string DefaultConfigJson() {
    return
        "{\n"
        "  \"refreshIntervalSeconds\": 300,\n"
        "  \"flyoutStyle\": \"acrylic\",\n"
        "  \"codexProfiles\": [\n"
        "    {\n"
        "      \"label\": \"Main\",\n"
        "      \"codexHome\": \"%APPDATA%\\\\Codex SWBar Windows\\\\profiles\\\\main\",\n"
        "      \"enabled\": true\n"
        "    }\n"
        "  ],\n"
        "  \"claude\": {\n"
        "    \"enabled\": true\n"
        "  }\n"
        "}\n";
}

AppConfig LoadConfig() {
    AppConfig config;
    std::wstring path = ConfigPath();
    if (!FileExists(path)) {
        WriteTextFileUtf8(path, DefaultConfigJson());
    }

    std::string json = ReadTextFileUtf8(path);
    config.refreshIntervalSeconds = std::min(86400, std::max(30, RegexInt(json, "refreshIntervalSeconds", 300)));
    config.flyoutStyle = Utf8ToWide(RegexString(json, "flyoutStyle"));
    if (config.flyoutStyle != L"solid") config.flyoutStyle = L"acrylic";

    for (const auto& object : ExtractObjectsInArray(json, "codexProfiles")) {
        CodexProfileConfig profile;
        profile.label = Utf8ToWide(RegexString(object, "label"));
        profile.codexHome = ExpandEnv(Utf8ToWide(RegexString(object, "codexHome")));
        profile.enabled = RegexBool(object, "enabled", true);
        if (profile.label.empty()) profile.label = L"Codex";
        if (profile.codexHome.empty()) profile.codexHome = DefaultCodexProfileHome();
        config.codexProfiles.push_back(profile);
    }

    if (config.codexProfiles.empty()) {
        config.codexProfiles.push_back({L"Main", DefaultCodexProfileHome(), true});
    }

    std::string claudeObject = ExtractObjectForKey(json, "claude");
    if (!claudeObject.empty()) {
        config.claude.enabled = RegexBool(claudeObject, "enabled", true);
    }

    return config;
}

std::string JsonEscape(const std::string& value) {
    std::string out;
    out.reserve(value.size() + 8);
    for (unsigned char ch : value) {
        switch (ch) {
            case '"': out += "\\\""; break;
            case '\\': out += "\\\\"; break;
            case '\b': out += "\\b"; break;
            case '\f': out += "\\f"; break;
            case '\n': out += "\\n"; break;
            case '\r': out += "\\r"; break;
            case '\t': out += "\\t"; break;
            default:
                if (ch < 0x20) {
                    char buffer[7]{};
                    std::snprintf(buffer, sizeof(buffer), "\\u%04x", ch);
                    out += buffer;
                } else {
                    out += static_cast<char>(ch);
                }
                break;
        }
    }
    return out;
}

std::wstring CollapsePathForConfig(const std::wstring& path) {
    std::wstring appData = GetEnvVar(L"APPDATA");
    auto isPathSeparator = [](wchar_t ch) {
        return ch == L'\\' || ch == L'/';
    };
    bool insideAppData = !appData.empty() &&
                         path.size() >= appData.size() &&
                         _wcsnicmp(path.c_str(), appData.c_str(), appData.size()) == 0 &&
                         (path.size() == appData.size() || isPathSeparator(path[appData.size()]));
    if (insideAppData) {
        return L"%APPDATA%" + path.substr(appData.size());
    }
    return path;
}

std::string SerializeConfig(const AppConfig& config) {
    std::ostringstream json;
    json << "{\n";
    json << "  \"refreshIntervalSeconds\": " << std::min(86400, std::max(30, config.refreshIntervalSeconds)) << ",\n";
    json << "  \"flyoutStyle\": \"" << (config.flyoutStyle == L"solid" ? "solid" : "acrylic") << "\",\n";
    json << "  \"codexProfiles\": [\n";
    for (size_t i = 0; i < config.codexProfiles.size(); ++i) {
        const auto& profile = config.codexProfiles[i];
        json << "    {\n";
        json << "      \"label\": \"" << JsonEscape(WideToUtf8(profile.label)) << "\",\n";
        json << "      \"codexHome\": \"" << JsonEscape(WideToUtf8(CollapsePathForConfig(profile.codexHome))) << "\",\n";
        json << "      \"enabled\": " << (profile.enabled ? "true" : "false") << "\n";
        json << "    }" << (i + 1 < config.codexProfiles.size() ? "," : "") << "\n";
    }
    json << "  ],\n";
    json << "  \"claude\": {\n";
    json << "    \"enabled\": " << (config.claude.enabled ? "true" : "false") << "\n";
    json << "  }\n";
    json << "}\n";
    return json.str();
}

bool SaveConfig(const AppConfig& config) {
    return WriteTextFileUtf8(ConfigPath(), SerializeConfig(config));
}

std::wstring SlugForProfileHome(const std::wstring& label) {
    std::wstring slug;
    for (wchar_t ch : label) {
        if ((ch >= L'a' && ch <= L'z') || (ch >= L'0' && ch <= L'9')) {
            slug += ch;
        } else if (ch >= L'A' && ch <= L'Z') {
            slug += static_cast<wchar_t>(std::towlower(ch));
        } else if (ch == L'-' || ch == L'_') {
            slug += ch;
        } else if (!slug.empty() && slug.back() != L'-') {
            slug += L'-';
        }
    }
    while (!slug.empty() && slug.back() == L'-') slug.pop_back();
    if (slug.empty()) slug = L"profile";
    return slug;
}

std::wstring DefaultCodexProfileHomeForLabel(const std::wstring& label, const AppConfig& config) {
    std::wstring base = ConfigDir() + L"\\profiles\\" + SlugForProfileHome(label);
    std::wstring candidate = base;
    for (int suffix = 2; suffix < 1000; ++suffix) {
        bool used = false;
        for (const auto& profile : config.codexProfiles) {
            if (_wcsicmp(profile.codexHome.c_str(), candidate.c_str()) == 0) {
                used = true;
                break;
            }
        }
        if (!used) return candidate;
        candidate = base + L"-" + std::to_wstring(suffix);
    }
    return base + L"-new";
}

struct UsageRow {
    std::wstring provider;
    std::wstring label;
    std::wstring identity;
    std::wstring plan;
    int primaryPercent = -1;
    int secondaryPercent = -1;
    std::wstring credits;
    std::wstring status;
    std::wstring error;
    int profileIndex = -1;
    SYSTEMTIME updatedAt{};
};

struct LoginNotice {
    bool success = false;
    bool informational = false;
    std::wstring message;
};

std::vector<std::string> Lines(const std::string& text) {
    std::vector<std::string> lines;
    std::istringstream stream(text);
    std::string line;
    while (std::getline(stream, line)) {
        line = TrimUtf8(line);
        if (!line.empty()) lines.push_back(line);
    }
    return lines;
}

void EnsureCodexProfileHome(const std::wstring& codexHome) {
    EnsureDirectory(codexHome);
    std::wstring configPath = codexHome + L"\\config.toml";
    if (!FileExists(configPath)) {
        WriteTextFileUtf8(
            configPath,
            "cli_auth_credentials_store = \"file\"\n"
            "service_tier = \"fast\"\n"
        );
    }
}

UsageRow FetchCodexProfile(const CodexProfileConfig& profile, const std::atomic_bool* cancelFlag) {
    UsageRow row;
    row.provider = L"Codex";
    row.label = profile.label;
    row.status = L"Refreshing";
    GetLocalTime(&row.updatedAt);

    EnsureCodexProfileHome(profile.codexHome);

    std::string input;
    input += "{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"Codex SWBar Windows\",\"version\":\"0.1.0\"}}}\n";
    input += "{\"method\":\"initialized\",\"params\":{}}\n";
    input += "{\"id\":2,\"method\":\"account/read\",\"params\":{\"refreshToken\":false}}\n";
    input += "{\"id\":3,\"method\":\"account/rateLimits/read\"}\n";

    std::wstring cmd = ShellCommand(L"codex -s read-only -a untrusted app-server");
    std::map<std::wstring, std::wstring, CaseInsensitiveLess> env{{L"CODEX_HOME", profile.codexHome}};

    ProcessResult process = RunProcess(cmd, input, 30000, "", 15000, env, 3, cancelFlag);

    if (process.stdoutText.empty()) {
        row.status = L"Error";
        std::string err = TrimUtf8(process.stderrText);
        row.error = process.timedOut
            ? L"Codex app-server did not answer within the timeout window."
            : (err.empty() ? L"No output from codex app-server." : Utf8ToWide(err.substr(0, 240)));
        return row;
    }

    bool sawAccount = false;
    bool sawRateLimits = false;
    std::wstring warning;

    for (const auto& line : Lines(process.stdoutText)) {
        if (line.find("\"method\":\"configWarning\"") != std::string::npos) {
            std::string summary = RegexString(line, "summary");
            if (!summary.empty()) warning = Utf8ToWide(summary);
        }

        if (IsJsonRpcResponseLine(line, 2)) {
            std::string email = RegexString(line, "email");
            std::string plan = RegexString(line, "planType");
            if (!email.empty()) {
                row.identity = Utf8ToWide(email);
                row.plan = Utf8ToWide(plan.empty() ? "unknown" : plan);
                sawAccount = true;
            }
        }

        if (IsJsonRpcResponseLine(line, 3)) {
            if (HasTopLevelField(line, "error")) {
                std::string message = RegexString(line, "message");
                if (!message.empty()) {
                    warning = Utf8ToWide(message);
                }
                continue;
            }

            std::string primary = ExtractObjectForKey(line, "primary");
            std::string secondary = ExtractObjectForKey(line, "secondary");
            int primaryPercent = RegexInt(primary, "usedPercent", -1);
            int secondaryPercent = RegexInt(secondary, "usedPercent", -1);
            std::string balance = RegexString(line, "balance");
            if (primaryPercent >= 0 || secondaryPercent >= 0 || !balance.empty()) {
                row.primaryPercent = primaryPercent;
                row.secondaryPercent = secondaryPercent;
                if (!balance.empty()) row.credits = Utf8ToWide(balance);
                sawRateLimits = true;
            }
        }
    }

    if (sawAccount && sawRateLimits) {
        row.status = L"OK";
    } else if (sawAccount) {
        row.status = L"Account OK";
        row.error = warning.empty()
            ? L"Quota RPC did not return yet; account/plan is available."
            : warning + L"; quota RPC did not return yet.";
    } else {
        row.status = L"Needs login";
        row.error = L"Could not read a Codex account from this CODEX_HOME.";
    }

    return row;
}

UsageRow FetchClaude(const ClaudeConfig& config, const std::atomic_bool* cancelFlag) {
    UsageRow row;
    row.provider = L"Claude";
    row.label = L"Default";
    GetLocalTime(&row.updatedAt);

    if (!config.enabled) {
        row.status = L"Disabled";
        return row;
    }

    ProcessResult version = RunProcess(ShellCommand(L"claude --version"), "", 10000, "", 0, {}, -1, cancelFlag);
    if (version.timedOut || version.stdoutText.empty()) {
        row.status = L"CLI missing";
        row.error = L"Could not run claude --version.";
        return row;
    }

    std::wstring user = GetEnvVar(L"USERPROFILE");
    bool hasDotClaude = FileExists(user + L"\\.claude\\.credentials.json");
    bool hasConfigClaude = FileExists(user + L"\\.config\\claude\\.credentials.json");

    row.identity = Trim(Utf8ToWide(TrimUtf8(version.stdoutText)));
    row.plan = L"CLI";
    if (hasDotClaude || hasConfigClaude) {
        row.status = L"Ready";
        row.error = L"Usage bridge pending: OAuth/cookie/PTY implementation next.";
    } else {
        row.status = L"Needs login";
        row.error = L"Claude CLI is installed, but no local credentials file was found.";
    }
    return row;
}

struct AppState {
    HINSTANCE instance = nullptr;
    HWND mainWindow = nullptr;
    HWND taskbarPresenceWindow = nullptr;
    HWND codexBarFlyoutWindow = nullptr;
    HWND contextMenuWindow = nullptr;
    HWND settingsWindow = nullptr;
    HWND taskbarParentWindow = nullptr;
    std::vector<HWND> taskbarPresenceWindows;
    NOTIFYICONDATAW tray{};
    HICON icon = nullptr;
    HICON smallIcon = nullptr;
    AppConfig config;
    std::vector<UsageRow> rows;
    std::vector<HitTarget> hitTargets;
    TargetKey hoverTarget;
    TargetKey pressedTarget;
    TargetKey flyoutHoverTarget;
    TargetKey flyoutPressedTarget;
    TargetKey contextMenuHoverTarget;
    TargetKey contextMenuPressedTarget;
    bool taskbarPresenceHover = false;
    bool taskbarPresencePressed = false;
    bool taskbarPresenceTrackingMouseLeave = false;
    RECT taskbarPresenceScreenRect{};
    bool flyoutTrackingMouseLeave = false;
    bool flyoutAcrylicActive = false;
    ULONGLONG flyoutOpenedTick = 0;
    ULONGLONG flyoutLastInteractionTick = 0;
    SYSTEMTIME lastRefreshLocal{};
    bool hasLastRefresh = false;
    HFONT settingsFont = nullptr;
    HFONT settingsTitleFont = nullptr;
    UINT settingsFontDpi = 0;
    std::vector<CodexProfileConfig> menuLoginProfiles;
    std::deque<std::wstring> pendingLoginUrls;
    std::deque<LoginNotice> pendingLoginNotices;
    std::mutex rowsMutex;
    std::mutex uiQueueMutex;
    int activeRefreshIntervalSeconds = 0;
    bool trackingMouseLeave = false;
    std::atomic_bool refreshPending = false;
    std::atomic_bool refreshing = false;
    std::atomic_bool loggingIn = false;
    std::atomic_bool shuttingDown = false;
    std::thread refreshThread;
    std::thread loginThread;
};

AppState g_app;

struct TaskbarPresenceState {
    HWND taskbar = nullptr;
    RECT hostScreenRect{};
    RECT widgetClientRect{};
    RECT widgetScreenRect{};
    bool compact = false;
    bool vertical = false;
};

TaskbarPresenceState* GetTaskbarPresenceState(HWND hwnd) {
    return reinterpret_cast<TaskbarPresenceState*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
}

bool IsMeaningfulRect(const RECT& rect) {
    return rect.right - rect.left > 8 && rect.bottom - rect.top > 8;
}

ULONGLONG NowTickMs() {
    return static_cast<ULONGLONG>(GetTickCount());
}

void RefreshAsync();
void ShowSettingsWindow();
void ShowContextMenu(HWND hwnd);
void OpenConfigFile(HWND hwnd);
void OpenProfilesFolder(HWND hwnd);
HWND EnsureSettingsWindow();
void PopulateSettingsWindow(HWND hwnd, bool preserveUserValues = false);
void ShowCodexBarFlyout(HWND sourcePresence);
void ToggleCodexBarFlyout(HWND sourcePresence = nullptr);
void HideCodexBarFlyout();
void UpdateTaskbarPresence();
void RecreateTaskbarPresence(HWND owner);
void DestroyTaskbarPresence();
LRESULT CALLBACK TaskbarPresenceProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam);
LRESULT CALLBACK CodexBarFlyoutProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam);
LRESULT CALLBACK ContextMenuProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam);
LRESULT CALLBACK SettingsProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam);
std::wstring ProfilePathLabel(const std::wstring& path);
bool IsWindowsDarkMode();
COLORREF Rgb(int r, int g, int b);
COLORREF BlendColor(COLORREF from, COLORREF to, int toPercent);
COLORREF AdjustColor(COLORREF color, int delta);
COLORREF WindowsAccentColor(COLORREF fallback);
void DrawTextLine(HDC dc, const std::wstring& text, RECT rect, COLORREF color, HFONT font, UINT format);
void FillRectColor(HDC dc, RECT rect, COLORREF color);
void DrawRoundRectColor(HDC dc, RECT rect, int radius, COLORREF fill, COLORREF border);
void DrawRoundRectOutline(HDC dc, RECT rect, int radius, COLORREF color);
void DrawButton(HDC dc, RECT rect, const std::wstring& text, HFONT font, bool primary, bool disabled, bool hovered, bool pressed);
void DrawStatusDot(HDC dc, int centerX, int centerY, int radius, COLORREF fill);
void ApplyDarkControlTheme(HWND hwnd);

struct FluentPalette {
    bool dark = false;
    COLORREF page{};
    COLORREF surface{};
    COLORREF surfaceAlt{};
    COLORREF elevated{};
    COLORREF elevatedHover{};
    COLORREF control{};
    COLORREF controlHover{};
    COLORREF controlPressed{};
    COLORREF border{};
    COLORREF borderStrong{};
    COLORREF text{};
    COLORREF muted{};
    COLORREF subtle{};
    COLORREF accent{};
    COLORREF accentSoft{};
    COLORREF accentHover{};
    COLORREF accentPressed{};
    COLORREF accentText{};
    COLORREF success{};
    COLORREF successSoft{};
    COLORREF warning{};
    COLORREF warningSoft{};
    COLORREF danger{};
    COLORREF dangerSoft{};
    COLORREF shadow{};
    COLORREF taskbar{};
};

FluentPalette CurrentPalette();

UINT GetDpiForHwnd(HWND hwnd) {
    HMODULE user32 = GetModuleHandleW(L"user32.dll");
    if (user32) {
        using GetDpiForWindowFn = UINT (WINAPI*)(HWND);
#if defined(__GNUC__)
#pragma GCC diagnostic push
#pragma GCC diagnostic ignored "-Wcast-function-type"
#endif
        auto getDpiForWindow = reinterpret_cast<GetDpiForWindowFn>(GetProcAddress(user32, "GetDpiForWindow"));
#if defined(__GNUC__)
#pragma GCC diagnostic pop
#endif
        if (getDpiForWindow && hwnd) return getDpiForWindow(hwnd);
    }

    HDC dc = hwnd ? GetDC(hwnd) : GetDC(nullptr);
    UINT dpi = dc ? static_cast<UINT>(GetDeviceCaps(dc, LOGPIXELSX)) : 96;
    if (dc) ReleaseDC(hwnd, dc);
    return dpi == 0 ? 96 : dpi;
}

int ScaleForDpi(int value, UINT dpi) {
    return MulDiv(value, static_cast<int>(dpi), 96);
}

int GetSystemMetricForDpi(int metric, UINT dpi) {
    HMODULE user32 = GetModuleHandleW(L"user32.dll");
    if (user32) {
        using GetSystemMetricsForDpiFn = int (WINAPI*)(int, UINT);
#if defined(__GNUC__)
#pragma GCC diagnostic push
#pragma GCC diagnostic ignored "-Wcast-function-type"
#endif
        auto getSystemMetricsForDpi = reinterpret_cast<GetSystemMetricsForDpiFn>(GetProcAddress(user32, "GetSystemMetricsForDpi"));
#if defined(__GNUC__)
#pragma GCC diagnostic pop
#endif
        if (getSystemMetricsForDpi) return getSystemMetricsForDpi(metric, dpi);
    }

    int value = GetSystemMetrics(metric);
    return dpi == 96 ? value : ScaleForDpi(value, dpi);
}

HICON LoadSharedAppIcon(int width, int height) {
    return reinterpret_cast<HICON>(LoadImageW(
        g_app.instance,
        MAKEINTRESOURCEW(IDI_APP_ICON),
        IMAGE_ICON,
        std::max(1, width),
        std::max(1, height),
        LR_DEFAULTCOLOR | LR_SHARED
    ));
}

void LoadAppIconsForDpi(UINT dpi) {
    int largeWidth = GetSystemMetricForDpi(SM_CXICON, dpi);
    int largeHeight = GetSystemMetricForDpi(SM_CYICON, dpi);
    int smallWidth = GetSystemMetricForDpi(SM_CXSMICON, dpi);
    int smallHeight = GetSystemMetricForDpi(SM_CYSMICON, dpi);

    g_app.icon = LoadSharedAppIcon(largeWidth, largeHeight);
    g_app.smallIcon = LoadSharedAppIcon(smallWidth, smallHeight);
}

void ApplyWindowIcons(HWND hwnd) {
    LoadAppIconsForDpi(GetDpiForHwnd(hwnd));
    if (g_app.icon) SendMessageW(hwnd, WM_SETICON, ICON_BIG, reinterpret_cast<LPARAM>(g_app.icon));
    if (g_app.smallIcon) SendMessageW(hwnd, WM_SETICON, ICON_SMALL, reinterpret_cast<LPARAM>(g_app.smallIcon));
}

void TrySetDwmAttribute(HWND hwnd, DWORD attribute, const void* value, DWORD valueSize) {
    HMODULE dwm = LoadLibraryW(L"dwmapi.dll");
    if (!dwm) return;
    using DwmSetWindowAttributeFn = HRESULT (WINAPI*)(HWND, DWORD, LPCVOID, DWORD);
#if defined(__GNUC__)
#pragma GCC diagnostic push
#pragma GCC diagnostic ignored "-Wcast-function-type"
#endif
    auto setAttribute = reinterpret_cast<DwmSetWindowAttributeFn>(GetProcAddress(dwm, "DwmSetWindowAttribute"));
#if defined(__GNUC__)
#pragma GCC diagnostic pop
#endif
    if (setAttribute) setAttribute(hwnd, attribute, value, valueSize);
    FreeLibrary(dwm);
}

DWORD AccentGradientColor(COLORREF color, BYTE alpha) {
    return (static_cast<DWORD>(alpha) << 24) |
           (static_cast<DWORD>(GetBValue(color)) << 16) |
           (static_cast<DWORD>(GetGValue(color)) << 8) |
           static_cast<DWORD>(GetRValue(color));
}

bool TryApplyAcrylicAccent(HWND hwnd, COLORREF tint, BYTE opacity, bool enable = true) {
    HMODULE user32 = GetModuleHandleW(L"user32.dll");
    if (!user32) return false;

    struct AccentPolicy {
        int state = 0;
        int flags = 0;
        DWORD gradientColor = 0;
        int animationId = 0;
    };
    struct CompositionAttributeData {
        int attribute = 0;
        PVOID data = nullptr;
        SIZE_T sizeOfData = 0;
    };
    using SetWindowCompositionAttributeFn = BOOL (WINAPI*)(HWND, CompositionAttributeData*);
#if defined(__GNUC__)
#pragma GCC diagnostic push
#pragma GCC diagnostic ignored "-Wcast-function-type"
#endif
    auto setCompositionAttribute = reinterpret_cast<SetWindowCompositionAttributeFn>(
        GetProcAddress(user32, "SetWindowCompositionAttribute")
    );
#if defined(__GNUC__)
#pragma GCC diagnostic pop
#endif
    if (!setCompositionAttribute) return false;

    AccentPolicy accent{};
    accent.state = enable ? 4 : 0;
    accent.flags = enable ? 2 : 0;
    accent.gradientColor = enable ? AccentGradientColor(tint, opacity) : 0;

    CompositionAttributeData data{};
    data.attribute = 19;
    data.data = &accent;
    data.sizeOfData = sizeof(accent);
    return setCompositionAttribute(hwnd, &data) != FALSE;
}

void ApplyFluentWindowBackdrop(HWND hwnd, bool transient) {
    if (!hwnd) return;

    BOOL dark = IsWindowsDarkMode() ? TRUE : FALSE;
    TrySetDwmAttribute(hwnd, 20, &dark, sizeof(dark));
    TrySetDwmAttribute(hwnd, 19, &dark, sizeof(dark));

    DWORD roundCorners = 2;
    TrySetDwmAttribute(hwnd, 33, &roundCorners, sizeof(roundCorners));

    DWORD backdrop = transient ? 3 : 2;
    TrySetDwmAttribute(hwnd, 38, &backdrop, sizeof(backdrop));

    if (transient) {
        COLORREF tint = dark ? Rgb(32, 32, 36) : Rgb(243, 243, 243);
        TryApplyAcrylicAccent(hwnd, tint, dark ? 210 : 220);
    }
}

struct UiScale {
    UINT dpi = 96;
    int operator()(int value) const {
        return ScaleForDpi(value, dpi);
    }
};

bool SameTarget(const TargetKey& key, UiAction action, int profileIndex) {
    return key.valid && key.action == action && key.profileIndex == profileIndex;
}

bool HitTargetAtPoint(POINT point, HitTarget& hit) {
    for (const auto& target : g_app.hitTargets) {
        if (PtInRect(&target.rect, point)) {
            hit = target;
            return true;
        }
    }
    return false;
}

bool UpdateHoverTarget(HWND hwnd, POINT point) {
    HitTarget hit;
    TargetKey next;
    if (HitTargetAtPoint(point, hit)) {
        next = TargetKey{true, hit.action, hit.profileIndex};
        SetCursor(LoadCursorW(nullptr, IDC_HAND));
    } else {
        next = TargetKey{};
    }

    bool changed = g_app.hoverTarget.valid != next.valid ||
                   g_app.hoverTarget.action != next.action ||
                   g_app.hoverTarget.profileIndex != next.profileIndex;
    if (changed) {
        g_app.hoverTarget = next;
        InvalidateRect(hwnd, nullptr, FALSE);
    }
    return next.valid;
}

void ClearInteractiveTargets(HWND hwnd) {
    bool changed = g_app.hoverTarget.valid || g_app.pressedTarget.valid;
    g_app.hoverTarget = TargetKey{};
    g_app.pressedTarget = TargetKey{};
    g_app.trackingMouseLeave = false;
    if (changed) InvalidateRect(hwnd, nullptr, FALSE);
}

void PostLoginUrlToUi(const std::wstring& url) {
    {
        std::lock_guard<std::mutex> lock(g_app.uiQueueMutex);
        g_app.pendingLoginUrls.push_back(url);
    }
    HWND hwnd = g_app.mainWindow;
    if (hwnd && IsWindow(hwnd)) {
        PostMessageW(hwnd, WM_LOGIN_OPEN_URL, 0, 0);
    }
}

void PostLoginNoticeToUi(bool success, const std::wstring& message, bool informational = false) {
    {
        std::lock_guard<std::mutex> lock(g_app.uiQueueMutex);
        g_app.pendingLoginNotices.push_back(LoginNotice{success, informational, message});
    }
    HWND hwnd = g_app.mainWindow;
    if (hwnd && IsWindow(hwnd)) {
        PostMessageW(hwnd, WM_LOGIN_DONE, 0, 0);
    }
}

void PostRefreshRequestToUi() {
    HWND hwnd = g_app.mainWindow;
    if (hwnd && IsWindow(hwnd)) {
        PostMessageW(hwnd, WM_REFRESH_REQUEST, 0, 0);
    }
}

bool StartsWithIgnoreCase(const std::wstring& value, const wchar_t* prefix) {
    size_t prefixLength = wcslen(prefix);
    return value.size() >= prefixLength && _wcsnicmp(value.c_str(), prefix, prefixLength) == 0;
}

bool StartsWithHostBoundary(const std::wstring& value, const wchar_t* prefix) {
    size_t prefixLength = wcslen(prefix);
    if (!StartsWithIgnoreCase(value, prefix)) return false;
    if (value.size() == prefixLength) return true;
    wchar_t next = value[prefixLength];
    return next == L'/' || next == L':' || next == L'?' || next == L'#';
}

bool IsSafeLoginUrl(const std::wstring& url) {
    std::wstring target = Trim(url);
    return StartsWithIgnoreCase(target, L"https://") ||
           StartsWithHostBoundary(target, L"http://localhost") ||
           StartsWithHostBoundary(target, L"http://127.0.0.1") ||
           StartsWithHostBoundary(target, L"http://[::1]");
}

struct LoginProcessResult {
    bool success = false;
    bool timedOut = false;
    std::wstring message;
};

LoginProcessResult RunCodexLogin(const CodexProfileConfig& profile) {
    LoginProcessResult login;
    EnsureCodexProfileHome(profile.codexHome);

    SECURITY_ATTRIBUTES sa{};
    sa.nLength = sizeof(sa);
    sa.bInheritHandle = TRUE;

    HANDLE stdoutRead = nullptr, stdoutWrite = nullptr;
    HANDLE stderrRead = nullptr, stderrWrite = nullptr;
    HANDLE stdinRead = nullptr, stdinWrite = nullptr;
    HANDLE job = nullptr;
    std::unique_lock<std::mutex> childCreationLock(g_childProcessInheritanceMutex);

    if (!CreatePipe(&stdoutRead, &stdoutWrite, &sa, 0)) {
        login.message = L"Could not create app-server pipes.";
        return login;
    }
    if (!CreatePipe(&stderrRead, &stderrWrite, &sa, 0)) {
        CloseHandleIfOpen(stdoutRead);
        CloseHandleIfOpen(stdoutWrite);
        login.message = L"Could not create app-server pipes.";
        return login;
    }
    if (!CreatePipe(&stdinRead, &stdinWrite, &sa, 0)) {
        CloseHandleIfOpen(stdoutRead);
        CloseHandleIfOpen(stdoutWrite);
        CloseHandleIfOpen(stderrRead);
        CloseHandleIfOpen(stderrWrite);
        login.message = L"Could not create app-server pipes.";
        return login;
    }

    SetHandleInformation(stdoutRead, HANDLE_FLAG_INHERIT, 0);
    SetHandleInformation(stderrRead, HANDLE_FLAG_INHERIT, 0);
    SetHandleInformation(stdinWrite, HANDLE_FLAG_INHERIT, 0);

    STARTUPINFOW si{};
    si.cb = sizeof(si);
    si.dwFlags = STARTF_USESTDHANDLES | STARTF_USESHOWWINDOW;
    si.wShowWindow = SW_HIDE;
    si.hStdOutput = stdoutWrite;
    si.hStdError = stderrWrite;
    si.hStdInput = stdinRead;

    PROCESS_INFORMATION pi{};
    std::wstring command = ShellCommand(L"codex app-server");
    std::map<std::wstring, std::wstring, CaseInsensitiveLess> envOverrides{{L"CODEX_HOME", profile.codexHome}};
    std::wstring envBlock = BuildEnvironmentBlock(envOverrides);

    BOOL ok = CreateProcessW(nullptr, command.data(), nullptr, nullptr, TRUE, CREATE_NO_WINDOW | CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT, envBlock.data(), nullptr, &si, &pi);
    DWORD processError = ok ? ERROR_SUCCESS : GetLastError();

    CloseHandle(stdoutWrite);
    CloseHandle(stderrWrite);
    CloseHandle(stdinRead);

    if (ok) {
        AssignToKillJob(job, pi.hProcess);
        if (!job) {
            processError = ERROR_ACCESS_DENIED;
            TerminateProcess(pi.hProcess, 1);
            ok = FALSE;
            SetLastError(ERROR_ACCESS_DENIED);
        } else if (ResumeThread(pi.hThread) == static_cast<DWORD>(-1)) {
            processError = GetLastError();
            TerminateProcessTree(job, pi.hProcess);
            ok = FALSE;
        }
    }

    if (!ok) {
        CloseHandle(stdoutRead);
        CloseHandle(stderrRead);
        CloseHandle(stdinWrite);
        CloseHandleIfOpen(job);
        if (pi.hThread) CloseHandle(pi.hThread);
        if (pi.hProcess) CloseHandle(pi.hProcess);
        login.message = L"Could not start codex app-server. Win32 error " + std::to_wstring(processError) + L".";
        return login;
    }
    childCreationLock.unlock();

    std::mutex outputMutex;
    std::string stdoutText;
    std::string stderrText;

    auto readToString = [&](HANDLE handle, std::string& target) {
        char buffer[4096];
        DWORD read = 0;
        while (ReadFile(handle, buffer, sizeof(buffer), &read, nullptr) && read > 0) {
            std::lock_guard<std::mutex> lock(outputMutex);
            target.append(buffer, buffer + read);
        }
    };

    std::thread stdoutThread([&] { readToString(stdoutRead, stdoutText); });
    std::thread stderrThread([&] { readToString(stderrRead, stderrText); });

    auto send = [&](const std::string& json) {
        DWORD written = 0;
        std::string line = json + "\n";
        WriteFile(stdinWrite, line.data(), static_cast<DWORD>(line.size()), &written, nullptr);
    };

    send("{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"Codex SWBar Windows\",\"version\":\"0.1.0\"}}}");
    send("{\"method\":\"initialized\",\"params\":{}}");
    send("{\"id\":2,\"method\":\"account/login/start\",\"params\":{\"type\":\"chatgpt\"}}");

    bool showedPrompt = false;
    bool completed = false;
    bool completedSuccess = false;
    std::wstring completionError;
    auto parseLoginOutput = [&](const std::string& snapshot) {
        for (const auto& line : Lines(snapshot)) {
            if (!showedPrompt && IsJsonRpcResponseLine(line, 2)) {
                std::string authUrl = RegexString(line, "authUrl");
                std::string verificationUrl = RegexString(line, "verificationUrl");
                std::string userCode = RegexString(line, "userCode");

                if (!verificationUrl.empty() && !userCode.empty()) {
                    showedPrompt = true;
                    std::wstring wideUrl = Utf8ToWide(verificationUrl);
                    PostLoginUrlToUi(wideUrl);
                    PostLoginNoticeToUi(
                        false,
                        L"Codex opened the verification page.\n\nEnter this code:\n" + Utf8ToWide(userCode),
                        true
                    );
                } else if (!authUrl.empty()) {
                    showedPrompt = true;
                    std::wstring wideUrl = Utf8ToWide(authUrl);
                    PostLoginUrlToUi(wideUrl);
                } else if (line.find("\"error\"") != std::string::npos) {
                    completed = true;
                    completedSuccess = false;
                    completionError = Utf8ToWide(RegexString(line, "message"));
                    break;
                }
            }

            if (line.find("\"method\":\"account/login/completed\"") != std::string::npos) {
                bool successValue = false;
                if (!TryRegexBool(line, "success", successValue) && RegexString(line, "error").empty()) {
                    continue;
                }
                completed = true;
                completedSuccess = successValue;
                completionError = Utf8ToWide(RegexString(line, "error"));
                break;
            }
        }
    };

    auto start = std::chrono::steady_clock::now();

    while (!g_app.shuttingDown && std::chrono::duration_cast<std::chrono::seconds>(std::chrono::steady_clock::now() - start).count() < 600) {
        std::string snapshot;
        {
            std::lock_guard<std::mutex> lock(outputMutex);
            snapshot = CompleteLinesOnly(stdoutText);
        }
        parseLoginOutput(snapshot);

        if (completed) {
            break;
        }
        if (WaitForSingleObject(pi.hProcess, 0) != WAIT_TIMEOUT) {
            break;
        }

        Sleep(200);
    }

    CloseHandle(stdinWrite);
    DWORD loginExitGraceMs = g_app.shuttingDown ? 300 : 3000;
    if (WaitForSingleObject(pi.hProcess, loginExitGraceMs) == WAIT_TIMEOUT) {
        TerminateProcessTree(job, pi.hProcess);
        WaitForSingleObject(pi.hProcess, g_app.shuttingDown ? 300 : 1000);
    }

    CloseHandleIfOpen(job);
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);

    if (stdoutThread.joinable()) stdoutThread.join();
    if (stderrThread.joinable()) stderrThread.join();
    CloseHandle(stdoutRead);
    CloseHandle(stderrRead);

    if (!completed) {
        std::string finalStdout;
        {
            std::lock_guard<std::mutex> lock(outputMutex);
            finalStdout = stdoutText;
        }
        parseLoginOutput(finalStdout);
    }

    if (!completed) {
        login.timedOut = true;
        login.message = showedPrompt
            ? L"Login prompt opened, but completion was not received within 10 minutes."
            : L"Login timed out or no login prompt was returned.";
    } else if (completedSuccess) {
        login.success = true;
        login.message = L"Codex login completed for " + profile.label + L".";
    } else {
        login.message = completionError.empty() ? L"Codex login failed." : completionError;
    }

    if (!login.success && login.message.empty()) {
        std::lock_guard<std::mutex> lock(outputMutex);
        login.message = stderrText.empty() ? L"Codex login failed." : Utf8ToWide(TrimUtf8(stderrText).substr(0, 300));
    }
    return login;
}

void LoginCodexProfileConfigAsync(const CodexProfileConfig& profile) {
    bool expected = false;
    if (!g_app.loggingIn.compare_exchange_strong(expected, true)) {
        MessageBoxW(g_app.mainWindow, L"A Codex login is already running.", kAppTitle, MB_OK | MB_ICONINFORMATION);
        return;
    }

    InvalidateRect(g_app.mainWindow, nullptr, FALSE);
    if (g_app.loginThread.joinable()) {
        g_app.loginThread.join();
    }
    g_app.loginThread = std::thread([profile] {
        LoginProcessResult result = RunCodexLogin(profile);
        if (!g_app.shuttingDown) {
            PostLoginNoticeToUi(result.success, result.message);
        }
        g_app.loggingIn = false;
        if (!g_app.shuttingDown) {
            PostRefreshRequestToUi();
        }
    });
}

void LoginCodexProfileAsync(size_t index) {
    AppConfig config;
    {
        std::lock_guard<std::mutex> lock(g_app.rowsMutex);
        config = g_app.config;
    }
    if (index >= config.codexProfiles.size()) {
        MessageBoxW(g_app.mainWindow, L"Codex profile not found.", kAppTitle, MB_OK | MB_ICONERROR);
        return;
    }
    LoginCodexProfileConfigAsync(config.codexProfiles[index]);
}

constexpr int CONTROL_PROMPT_EDIT = 3001;
constexpr int CONTROL_SETTINGS_REFRESH_EDIT = 4001;
constexpr int CONTROL_SETTINGS_CLAUDE_CHECK = 4002;
constexpr int CONTROL_SETTINGS_SAVE = 4003;
constexpr int CONTROL_SETTINGS_REFRESH_NOW = 4004;
constexpr int CONTROL_SETTINGS_ADD_PROFILE = 4005;
constexpr int CONTROL_SETTINGS_OPEN_CONFIG = 4006;
constexpr int CONTROL_SETTINGS_OPEN_PROFILES = 4007;
constexpr int CONTROL_SETTINGS_ACRYLIC_CHECK = 4008;
constexpr int CONTROL_SETTINGS_LOGIN_BASE = 4100;
constexpr int CONTROL_SETTINGS_RENAME_BASE = 4200;
constexpr int CONTROL_SETTINGS_TOGGLE_BASE = 4300;
constexpr int CONTROL_SETTINGS_FOLDER_BASE = 4400;
constexpr int CONTROL_SETTINGS_PROFILE_LIMIT = 99;

struct TextPromptState {
    std::wstring title;
    std::wstring label;
    std::wstring value;
    bool accepted = false;
    HWND edit = nullptr;
    HFONT titleFont = nullptr;
    HFONT bodyFont = nullptr;
    HFONT captionFont = nullptr;
    UINT fontDpi = 0;
};

std::wstring WindowText(HWND hwnd) {
    int length = GetWindowTextLengthW(hwnd);
    std::wstring text(static_cast<size_t>(length) + 1, L'\0');
    GetWindowTextW(hwnd, text.data(), length + 1);
    text.resize(static_cast<size_t>(length));
    return text;
}

const wchar_t* kCheckedProp = L"CodexSWBar.ControlChecked";

bool ControlChecked(HWND hwnd) {
    return hwnd && GetPropW(hwnd, kCheckedProp) != nullptr;
}

void SetControlChecked(HWND hwnd, bool checked) {
    if (!hwnd) return;
    if (checked) {
        SetPropW(hwnd, kCheckedProp, reinterpret_cast<HANDLE>(static_cast<INT_PTR>(1)));
    } else {
        RemovePropW(hwnd, kCheckedProp);
    }
    InvalidateRect(hwnd, nullptr, FALSE);
}

void DestroyTextPromptFonts(TextPromptState* state) {
    if (!state) return;
    if (state->titleFont) {
        DeleteObject(state->titleFont);
        state->titleFont = nullptr;
    }
    if (state->bodyFont) {
        DeleteObject(state->bodyFont);
        state->bodyFont = nullptr;
    }
    if (state->captionFont) {
        DeleteObject(state->captionFont);
        state->captionFont = nullptr;
    }
    state->fontDpi = 0;
}

void EnsureTextPromptFonts(HWND hwnd, TextPromptState* state) {
    if (!state) return;
    UINT dpi = GetDpiForHwnd(hwnd);
    if (state->titleFont && state->bodyFont && state->captionFont && state->fontDpi == dpi) return;
    DestroyTextPromptFonts(state);
    UiScale S{dpi};
    state->fontDpi = dpi;
    state->titleFont = CreateFontW(S(20), 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                                   OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                                   DEFAULT_PITCH, L"Segoe UI Variable");
    state->bodyFont = CreateFontW(S(14), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                                  OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                                  DEFAULT_PITCH, L"Segoe UI Variable");
    state->captionFont = CreateFontW(S(12), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                                     OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                                     DEFAULT_PITCH, L"Segoe UI");
}

RECT TextPromptEditFrame(HWND hwnd) {
    RECT client{};
    GetClientRect(hwnd, &client);
    UiScale S{GetDpiForHwnd(hwnd)};
    return RECT{S(24), S(94), client.right - S(24), S(132)};
}

RECT TextPromptEditChildRect(HWND hwnd) {
    UiScale S{GetDpiForHwnd(hwnd)};
    RECT rect = TextPromptEditFrame(hwnd);
    InflateRect(&rect, -S(10), -S(7));
    return rect;
}

RECT TextPromptButtonRect(HWND hwnd, bool primary) {
    RECT client{};
    GetClientRect(hwnd, &client);
    UiScale S{GetDpiForHwnd(hwnd)};
    int width = S(104);
    int height = S(34);
    int gap = S(10);
    int right = client.right - S(24);
    int top = client.bottom - S(52);
    if (!primary) right -= width + gap;
    return RECT{right - width, top, right, top + height};
}

void LayoutTextPromptChildren(HWND hwnd, TextPromptState* state) {
    if (!state) return;
    RECT edit = TextPromptEditChildRect(hwnd);
    if (state->edit) {
        MoveWindow(state->edit, edit.left, edit.top, edit.right - edit.left, edit.bottom - edit.top, TRUE);
    }
    HWND ok = GetDlgItem(hwnd, IDOK);
    HWND cancel = GetDlgItem(hwnd, IDCANCEL);
    RECT cancelRect = TextPromptButtonRect(hwnd, false);
    RECT okRect = TextPromptButtonRect(hwnd, true);
    if (cancel) MoveWindow(cancel, cancelRect.left, cancelRect.top, cancelRect.right - cancelRect.left, cancelRect.bottom - cancelRect.top, TRUE);
    if (ok) MoveWindow(ok, okRect.left, okRect.top, okRect.right - okRect.left, okRect.bottom - okRect.top, TRUE);
}

void PaintTextPromptWindow(HWND hwnd, TextPromptState* state) {
    PAINTSTRUCT ps{};
    HDC windowDc = BeginPaint(hwnd, &ps);
    RECT client{};
    GetClientRect(hwnd, &client);
    int width = std::max(1, static_cast<int>(client.right - client.left));
    int height = std::max(1, static_cast<int>(client.bottom - client.top));

    HDC bufferDc = CreateCompatibleDC(windowDc);
    HBITMAP bitmap = bufferDc ? CreateCompatibleBitmap(windowDc, width, height) : nullptr;
    HGDIOBJ oldBitmap = nullptr;
    HDC dc = windowDc;
    if (bufferDc && bitmap) {
        oldBitmap = SelectObject(bufferDc, bitmap);
        dc = bufferDc;
    }

    UiScale S{GetDpiForHwnd(hwnd)};
    EnsureTextPromptFonts(hwnd, state);
    FluentPalette palette = CurrentPalette();

    FillRectColor(dc, client, palette.page);

    RECT title{S(24), S(18), client.right - S(24), S(50)};
    DrawTextLine(dc, state ? state->title : L"", title, palette.text, state && state->titleFont ? state->titleFont : nullptr,
                 DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS);

    RECT label{S(24), S(58), client.right - S(24), S(84)};
    DrawTextLine(dc, state ? state->label : L"", label, palette.muted, state && state->captionFont ? state->captionFont : nullptr,
                 DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS);

    RECT editFrame = TextPromptEditFrame(hwnd);
    bool editFocused = state && state->edit && GetFocus() == state->edit;
    DrawRoundRectColor(dc, editFrame, S(8), palette.control, editFocused ? palette.accent : palette.borderStrong);

    RECT footer{S(24), client.bottom - S(70), client.right - S(24), client.bottom - S(69)};
    FillRectColor(dc, footer, palette.border);

    if (dc != windowDc) {
        BitBlt(windowDc, 0, 0, width, height, dc, 0, 0, SRCCOPY);
    }
    if (oldBitmap) SelectObject(bufferDc, oldBitmap);
    if (bitmap) DeleteObject(bitmap);
    if (bufferDc) DeleteDC(bufferDc);
    EndPaint(hwnd, &ps);
}

LRESULT CALLBACK TextPromptProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    auto* state = reinterpret_cast<TextPromptState*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
    switch (msg) {
        case WM_NCCREATE: {
            auto* create = reinterpret_cast<CREATESTRUCTW*>(lParam);
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(create->lpCreateParams));
            return TRUE;
        }

        case WM_CREATE: {
            state = reinterpret_cast<TextPromptState*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
            EnsureTextPromptFonts(hwnd, state);
            ApplyWindowIcons(hwnd);
            ApplyFluentWindowBackdrop(hwnd, false);
            RECT edit = TextPromptEditChildRect(hwnd);
            state->edit = CreateWindowExW(0, L"EDIT", state->value.c_str(),
                                          WS_CHILD | WS_VISIBLE | WS_TABSTOP | ES_AUTOHSCROLL,
                                          edit.left, edit.top, edit.right - edit.left, edit.bottom - edit.top, hwnd,
                                          reinterpret_cast<HMENU>(CONTROL_PROMPT_EDIT), g_app.instance, nullptr);
            RECT cancelRect = TextPromptButtonRect(hwnd, false);
            RECT okRect = TextPromptButtonRect(hwnd, true);
            HWND cancel = CreateWindowW(L"BUTTON", L"Cancel", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_OWNERDRAW,
                                        cancelRect.left, cancelRect.top, cancelRect.right - cancelRect.left,
                                        cancelRect.bottom - cancelRect.top, hwnd, reinterpret_cast<HMENU>(IDCANCEL),
                                        g_app.instance, nullptr);
            HWND ok = CreateWindowW(L"BUTTON", L"OK", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_OWNERDRAW,
                                    okRect.left, okRect.top, okRect.right - okRect.left, okRect.bottom - okRect.top,
                                    hwnd, reinterpret_cast<HMENU>(IDOK), g_app.instance, nullptr);
            if (state->edit) {
                SendMessageW(state->edit, WM_SETFONT, reinterpret_cast<WPARAM>(state->bodyFont), TRUE);
                ApplyDarkControlTheme(state->edit);
                SendMessageW(state->edit, EM_SETSEL, 0, -1);
            }
            if (ok) SendMessageW(ok, WM_SETFONT, reinterpret_cast<WPARAM>(state->bodyFont), TRUE);
            if (cancel) SendMessageW(cancel, WM_SETFONT, reinterpret_cast<WPARAM>(state->bodyFont), TRUE);
            SendMessageW(hwnd, DM_SETDEFID, IDOK, 0);
            return 0;
        }

        case WM_PAINT:
            PaintTextPromptWindow(hwnd, state);
            return 0;

        case WM_ERASEBKGND:
            return 1;

        case WM_DRAWITEM: {
            auto* item = reinterpret_cast<DRAWITEMSTRUCT*>(lParam);
            if (item && item->CtlType == ODT_BUTTON && item->hwndItem) {
                std::wstring text = WindowText(item->hwndItem);
                RECT rect = item->rcItem;
                UiScale S{GetDpiForHwnd(hwnd)};
                InflateRect(&rect, -S(1), -S(1));
                bool disabled = (item->itemState & ODS_DISABLED) != 0;
                bool pressed = (item->itemState & ODS_SELECTED) != 0;
                bool focused = (item->itemState & ODS_FOCUS) != 0;
                DrawButton(item->hDC, rect, text, state ? state->bodyFont : nullptr, item->CtlID == IDOK, disabled, focused, pressed);
                return TRUE;
            }
            break;
        }

        case WM_CTLCOLOREDIT: {
            HDC dc = reinterpret_cast<HDC>(wParam);
            bool dark = IsWindowsDarkMode();
            static HBRUSH darkBrush = CreateSolidBrush(RGB(54, 56, 62));
            static HBRUSH lightBrush = CreateSolidBrush(RGB(250, 251, 253));
            SetTextColor(dc, dark ? RGB(244, 245, 247) : RGB(29, 35, 45));
            SetBkColor(dc, dark ? RGB(54, 56, 62) : RGB(250, 251, 253));
            return reinterpret_cast<LRESULT>(dark ? darkBrush : lightBrush);
        }

        case WM_SIZE:
            LayoutTextPromptChildren(hwnd, state);
            InvalidateRect(hwnd, nullptr, FALSE);
            return 0;

        case WM_COMMAND:
            if (LOWORD(wParam) == CONTROL_PROMPT_EDIT &&
                (HIWORD(wParam) == EN_SETFOCUS || HIWORD(wParam) == EN_KILLFOCUS)) {
                InvalidateRect(hwnd, nullptr, FALSE);
                return 0;
            }
            if (LOWORD(wParam) == IDOK) {
                state->value = WindowText(state->edit);
                state->accepted = true;
                DestroyWindow(hwnd);
                return 0;
            }
            if (LOWORD(wParam) == IDCANCEL) {
                DestroyWindow(hwnd);
                return 0;
            }
            break;

        case WM_CLOSE:
            DestroyWindow(hwnd);
            return 0;

        case WM_DESTROY:
            DestroyTextPromptFonts(state);
            return 0;
    }
    return DefWindowProcW(hwnd, msg, wParam, lParam);
}

bool EnsureTextPromptClass() {
    static bool registered = false;
    if (registered) return true;

    WNDCLASSEXW klass{};
    klass.cbSize = sizeof(klass);
    klass.lpfnWndProc = TextPromptProc;
    klass.hInstance = g_app.instance;
    klass.lpszClassName = kTextPromptClass;
    klass.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    klass.hIcon = g_app.icon ? g_app.icon : LoadIconW(g_app.instance, MAKEINTRESOURCEW(IDI_APP_ICON));
    klass.hIconSm = g_app.smallIcon ? g_app.smallIcon : klass.hIcon;
    klass.hbrBackground = nullptr;
    registered = RegisterClassExW(&klass) != 0 || GetLastError() == ERROR_CLASS_ALREADY_EXISTS;
    return registered;
}

std::optional<std::wstring> PromptForText(HWND owner, const std::wstring& title, const std::wstring& label, const std::wstring& initialValue) {
    if (!EnsureTextPromptClass()) return std::nullopt;

    TextPromptState state{title, label, initialValue};
    UiScale S{GetDpiForHwnd(owner)};
    int width = S(520);
    int height = S(240);

    RECT ownerRect{};
    if (owner && IsWindow(owner)) {
        GetWindowRect(owner, &ownerRect);
    } else {
        SystemParametersInfoW(SPI_GETWORKAREA, 0, &ownerRect, 0);
    }
    int x = static_cast<int>(ownerRect.left) + std::max<int>(0, static_cast<int>(ownerRect.right - ownerRect.left - width) / 2);
    int y = static_cast<int>(ownerRect.top) + std::max<int>(0, static_cast<int>(ownerRect.bottom - ownerRect.top - height) / 2);

    HWND dialog = CreateWindowExW(
        WS_EX_CONTROLPARENT,
        kTextPromptClass,
        title.c_str(),
        WS_POPUP | WS_CAPTION | WS_SYSMENU | WS_CLIPCHILDREN,
        x,
        y,
        width,
        height,
        owner,
        nullptr,
        g_app.instance,
        &state
    );
    if (!dialog) return std::nullopt;

    if (owner && IsWindow(owner)) EnableWindow(owner, FALSE);
    ShowWindow(dialog, SW_SHOWNORMAL);
    SetForegroundWindow(dialog);
    if (state.edit) SetFocus(state.edit);

    MSG msg{};
    bool quit = false;
    while (IsWindow(dialog)) {
        BOOL got = GetMessageW(&msg, nullptr, 0, 0);
        if (got == 0) {
            quit = true;
            break;
        }
        if (got == -1) break;
        if (!IsDialogMessageW(dialog, &msg)) {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }

    if (owner && IsWindow(owner)) {
        EnableWindow(owner, TRUE);
        SetForegroundWindow(owner);
    }
    if (quit) PostQuitMessage(static_cast<int>(msg.wParam));
    return state.accepted ? std::optional<std::wstring>(state.value) : std::nullopt;
}

std::optional<int> ParseConfigInt(const std::wstring& text) {
    std::wstring trimmed = Trim(text);
    if (trimmed.empty()) return std::nullopt;
    try {
        size_t parsed = 0;
        int value = std::stoi(trimmed, &parsed);
        while (parsed < trimmed.size() && iswspace(trimmed[parsed])) parsed++;
        if (parsed != trimmed.size()) return std::nullopt;
        return value;
    } catch (...) {
        return std::nullopt;
    }
}

AppConfig CurrentConfigSnapshot() {
    std::lock_guard<std::mutex> lock(g_app.rowsMutex);
    return g_app.config.codexProfiles.empty() ? LoadConfig() : g_app.config;
}

bool SaveAndApplyConfig(HWND hwnd, const AppConfig& config, bool requestRefresh) {
    if (!SaveConfig(config)) {
        MessageBoxW(hwnd, L"Could not save config.json.", kAppTitle, MB_OK | MB_ICONERROR);
        return false;
    }

    {
        std::lock_guard<std::mutex> lock(g_app.rowsMutex);
        g_app.config = config;
    }

    if (config.refreshIntervalSeconds != g_app.activeRefreshIntervalSeconds) {
        g_app.activeRefreshIntervalSeconds = config.refreshIntervalSeconds;
        SetTimer(hwnd, TIMER_REFRESH, static_cast<UINT>(g_app.activeRefreshIntervalSeconds) * 1000u, nullptr);
    }

    InvalidateRect(hwnd, nullptr, FALSE);
    if (requestRefresh) RefreshAsync();
    return true;
}

void RenameProfileFromHud(HWND hwnd, size_t index) {
    AppConfig config = CurrentConfigSnapshot();
    if (index >= config.codexProfiles.size()) return;

    auto value = PromptForText(hwnd, L"Rename Codex profile", L"Profile display name", config.codexProfiles[index].label);
    if (!value) return;
    std::wstring label = Trim(*value);
    if (label.empty()) {
        MessageBoxW(hwnd, L"Profile name cannot be empty.", kAppTitle, MB_OK | MB_ICONWARNING);
        return;
    }

    config.codexProfiles[index].label = label;
    SaveAndApplyConfig(hwnd, config, true);
}

void ToggleProfileFromHud(HWND hwnd, size_t index) {
    AppConfig config = CurrentConfigSnapshot();
    if (index >= config.codexProfiles.size()) return;
    config.codexProfiles[index].enabled = !config.codexProfiles[index].enabled;
    SaveAndApplyConfig(hwnd, config, true);
}

void OpenProfileFolderFromHud(HWND hwnd, size_t index) {
    AppConfig config = CurrentConfigSnapshot();
    if (index >= config.codexProfiles.size()) return;
    EnsureDirectory(config.codexProfiles[index].codexHome);
    ShellExecuteW(hwnd, L"open", config.codexProfiles[index].codexHome.c_str(), nullptr, nullptr, SW_SHOWNORMAL);
}

void AddProfileFromHud(HWND hwnd) {
    AppConfig config = CurrentConfigSnapshot();
    auto labelValue = PromptForText(hwnd, L"New Codex profile", L"Profile display name", L"Second account");
    if (!labelValue) return;
    std::wstring label = Trim(*labelValue);
    if (label.empty()) {
        MessageBoxW(hwnd, L"Profile name cannot be empty.", kAppTitle, MB_OK | MB_ICONWARNING);
        return;
    }

    std::wstring defaultHome = DefaultCodexProfileHomeForLabel(label, config);
    auto homeValue = PromptForText(hwnd, L"New Codex profile", L"CODEX_HOME folder for this profile", ProfilePathLabel(defaultHome));
    if (!homeValue) return;
    std::wstring home = ExpandEnv(Trim(*homeValue));
    if (home.empty()) home = defaultHome;

    CodexProfileConfig profile{label, home, true};
    EnsureCodexProfileHome(profile.codexHome);
    config.codexProfiles.push_back(profile);
    if (!SaveAndApplyConfig(hwnd, config, false)) return;

    int loginNow = MessageBoxW(hwnd, L"Profile created. Start Codex login for it now?", kAppTitle, MB_YESNO | MB_ICONQUESTION);
    if (loginNow == IDYES) {
        LoginCodexProfileConfigAsync(profile);
    } else {
        RefreshAsync();
    }
}

COLORREF Rgb(int r, int g, int b) {
    return RGB(static_cast<BYTE>(r), static_cast<BYTE>(g), static_cast<BYTE>(b));
}

int ClampByte(int value) {
    return std::max(0, std::min(255, value));
}

COLORREF BlendColor(COLORREF from, COLORREF to, int toPercent) {
    int p = std::max(0, std::min(100, toPercent));
    int r = (GetRValue(from) * (100 - p) + GetRValue(to) * p) / 100;
    int g = (GetGValue(from) * (100 - p) + GetGValue(to) * p) / 100;
    int b = (GetBValue(from) * (100 - p) + GetBValue(to) * p) / 100;
    return Rgb(r, g, b);
}

COLORREF AdjustColor(COLORREF color, int delta) {
    return Rgb(
        ClampByte(static_cast<int>(GetRValue(color)) + delta),
        ClampByte(static_cast<int>(GetGValue(color)) + delta),
        ClampByte(static_cast<int>(GetBValue(color)) + delta)
    );
}

COLORREF WindowsAccentColor(COLORREF fallback) {
    HMODULE dwm = LoadLibraryW(L"dwmapi.dll");
    if (!dwm) return fallback;
    using DwmGetColorizationColorFn = HRESULT (WINAPI*)(DWORD*, BOOL*);
#if defined(__GNUC__)
#pragma GCC diagnostic push
#pragma GCC diagnostic ignored "-Wcast-function-type"
#endif
    auto getColor = reinterpret_cast<DwmGetColorizationColorFn>(GetProcAddress(dwm, "DwmGetColorizationColor"));
#if defined(__GNUC__)
#pragma GCC diagnostic pop
#endif
    DWORD packed = 0;
    BOOL opaque = FALSE;
    COLORREF color = fallback;
    if (getColor && SUCCEEDED(getColor(&packed, &opaque))) {
        color = Rgb(static_cast<int>((packed >> 16) & 0xFF), static_cast<int>((packed >> 8) & 0xFF), static_cast<int>(packed & 0xFF));
    }
    FreeLibrary(dwm);
    return color;
}

void DrawTextLine(HDC dc, const std::wstring& text, RECT rect, COLORREF color, HFONT font, UINT format) {
    HFONT oldFont = font ? reinterpret_cast<HFONT>(SelectObject(dc, font)) : nullptr;
    SetBkMode(dc, TRANSPARENT);
    SetTextColor(dc, color);
    DrawTextW(dc, text.c_str(), -1, &rect, format);
    if (oldFont) SelectObject(dc, oldFont);
}

void FillRectColor(HDC dc, RECT rect, COLORREF color) {
    HBRUSH brush = CreateSolidBrush(color);
    FillRect(dc, &rect, brush);
    DeleteObject(brush);
}

void DrawRoundRectColor(HDC dc, RECT rect, int radius, COLORREF fill, COLORREF border);

void WriteRefreshLog(const std::vector<UsageRow>& rows) {
    std::ostringstream log;
    log << "Codex SWBar Windows refresh\n";
    for (const auto& row : rows) {
        log << "- " << WideToUtf8(row.provider)
            << " / " << WideToUtf8(row.label)
            << " status=" << WideToUtf8(row.status);
        if (!row.identity.empty()) log << " identity=<redacted>";
        if (!row.plan.empty()) log << " plan=" << WideToUtf8(row.plan);
        if (row.primaryPercent >= 0) log << " primary=" << row.primaryPercent << "%";
        if (row.secondaryPercent >= 0) log << " secondary=" << row.secondaryPercent << "%";
        if (!row.error.empty()) log << " note=" << WideToUtf8(row.error);
        log << "\n";
    }
    WriteTextFileUtf8(ConfigDir() + L"\\last-refresh.log", log.str());
}

void AddHitTarget(const RECT& rect, UiAction action, int profileIndex = -1) {
    g_app.hitTargets.push_back({rect, action, profileIndex});
}

void DrawButton(
    HDC dc,
    RECT rect,
    const std::wstring& text,
    HFONT font,
    bool primary = false,
    bool disabled = false,
    bool hovered = false,
    bool pressed = false
) {
    if (disabled) {
        hovered = false;
        pressed = false;
    }

    FluentPalette palette = CurrentPalette();
    COLORREF fill = disabled
        ? BlendColor(palette.control, palette.page, 42)
        : (primary ? palette.accent : palette.control);
    COLORREF border = disabled
        ? palette.border
        : (primary ? palette.accentPressed : palette.border);
    COLORREF ink = disabled
        ? palette.subtle
        : (primary ? palette.accentText : palette.text);

    if (hovered) {
        fill = primary ? palette.accentHover : palette.controlHover;
    }
    if (pressed) {
        fill = primary ? palette.accentPressed : palette.controlPressed;
        ink = primary ? ink : palette.muted;
    }

    DrawRoundRectColor(dc, rect, 6, fill, border);
    RECT textRect = rect;
    InflateRect(&textRect, -10, 0);
    DrawTextLine(dc, text, textRect, ink, font, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS);
}

void DrawToggleSwitch(HDC dc, RECT rect, bool checked, bool focused, bool pressed, bool disabled) {
    FluentPalette palette = CurrentPalette();
    COLORREF track = checked
        ? (pressed ? palette.accentPressed : palette.accent)
        : (pressed ? palette.controlPressed : palette.control);
    COLORREF border = checked
        ? track
        : (focused ? palette.accent : palette.borderStrong);
    COLORREF thumb = checked
        ? (palette.dark ? Rgb(14, 24, 37) : Rgb(248, 251, 255))
        : palette.muted;

    if (disabled) {
        track = BlendColor(palette.control, palette.page, 42);
        border = palette.border;
        thumb = palette.subtle;
    }

    int height = std::max(1, static_cast<int>(rect.bottom - rect.top));
    DrawRoundRectColor(dc, rect, height, track, border);

    int thumbRadius = std::max(3, (height - 10) / 2);
    int centerY = rect.top + height / 2;
    int centerX = checked ? rect.right - height / 2 - 1 : rect.left + height / 2 + 1;
    if (pressed) centerX += checked ? -1 : 1;
    DrawStatusDot(dc, centerX, centerY, thumbRadius, thumb);

    if (focused) {
        RECT focus = rect;
        InflateRect(&focus, 2, 2);
        DrawRoundRectOutline(dc, focus, std::max(1, static_cast<int>(focus.bottom - focus.top)), palette.accent);
    }
}

void DrawStatusBadge(HDC dc, RECT rect, const std::wstring& status, HFONT font) {
    FluentPalette palette = CurrentPalette();
    COLORREF fill = palette.accentSoft;
    COLORREF border = BlendColor(palette.border, palette.accent, 28);
    COLORREF ink = palette.accent;

    if (status == L"OK" || status == L"Ready") {
        fill = palette.successSoft;
        border = BlendColor(palette.border, palette.success, 28);
        ink = palette.success;
    } else if (status == L"Needs login" || status == L"Error" || status == L"Timeout") {
        fill = palette.dangerSoft;
        border = BlendColor(palette.border, palette.danger, 28);
        ink = palette.danger;
    }

    DrawRoundRectColor(dc, rect, 10, fill, border);
    RECT textRect = rect;
    InflateRect(&textRect, -8, 0);
    DrawTextLine(dc, status, textRect, ink, font, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS);
}

void DrawQuotaBar(HDC dc, RECT rect, int percent, COLORREF fill) {
    FluentPalette palette = CurrentPalette();
    DrawRoundRectColor(dc, rect, 5, palette.border, palette.border);
    RECT inner = rect;
    InflateRect(&inner, -1, -1);
    DrawRoundRectColor(dc, inner, 4, palette.controlPressed, palette.controlPressed);
    if (percent >= 0) {
        RECT used = inner;
        int width = inner.right - inner.left;
        used.right = used.left + (width * std::min(100, std::max(0, percent))) / 100;
        if (used.right > used.left) {
            DrawRoundRectColor(dc, used, 4, fill, fill);
        }
    }
}

bool IsWindowsDarkMode() {
    DWORD appsUseLightTheme = 1;
    DWORD size = sizeof(appsUseLightTheme);
    LSTATUS status = RegGetValueW(
        HKEY_CURRENT_USER,
        L"Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize",
        L"AppsUseLightTheme",
        RRF_RT_REG_DWORD,
        nullptr,
        &appsUseLightTheme,
        &size
    );
    return status == ERROR_SUCCESS && appsUseLightTheme == 0;
}

FluentPalette CurrentPalette() {
    FluentPalette p;
    p.dark = IsWindowsDarkMode();
    COLORREF accent = WindowsAccentColor(p.dark ? Rgb(96, 172, 255) : Rgb(28, 102, 201));
    if (p.dark) {
        p.page = Rgb(32, 32, 36);
        p.surface = Rgb(43, 43, 48);
        p.surfaceAlt = Rgb(49, 50, 56);
        p.elevated = Rgb(52, 54, 60);
        p.elevatedHover = Rgb(60, 63, 70);
        p.control = Rgb(54, 56, 62);
        p.controlHover = Rgb(66, 69, 76);
        p.controlPressed = Rgb(45, 47, 53);
        p.border = Rgb(64, 66, 74);
        p.borderStrong = Rgb(82, 86, 96);
        p.text = Rgb(244, 245, 247);
        p.muted = Rgb(190, 196, 205);
        p.subtle = Rgb(136, 145, 158);
        p.accent = BlendColor(accent, Rgb(118, 198, 255), 28);
        p.accentSoft = BlendColor(p.surface, p.accent, 20);
        p.accentHover = AdjustColor(p.accent, 14);
        p.accentPressed = AdjustColor(p.accent, -20);
        p.accentText = Rgb(14, 24, 37);
        p.success = Rgb(86, 201, 143);
        p.successSoft = BlendColor(p.surface, p.success, 18);
        p.warning = Rgb(238, 183, 88);
        p.warningSoft = BlendColor(p.surface, p.warning, 18);
        p.danger = Rgb(242, 122, 99);
        p.dangerSoft = BlendColor(p.surface, p.danger, 18);
        p.shadow = Rgb(13, 14, 17);
        p.taskbar = Rgb(32, 32, 36);
    } else {
        p.page = Rgb(240, 243, 247);
        p.surface = Rgb(247, 249, 252);
        p.surfaceAlt = Rgb(238, 242, 247);
        p.elevated = Rgb(253, 254, 255);
        p.elevatedHover = Rgb(246, 249, 253);
        p.control = Rgb(250, 251, 253);
        p.controlHover = Rgb(239, 245, 253);
        p.controlPressed = Rgb(226, 236, 249);
        p.border = Rgb(213, 220, 230);
        p.borderStrong = Rgb(183, 194, 209);
        p.text = Rgb(29, 35, 45);
        p.muted = Rgb(84, 96, 113);
        p.subtle = Rgb(129, 143, 161);
        p.accent = BlendColor(accent, Rgb(24, 95, 191), 30);
        p.accentSoft = BlendColor(p.elevated, p.accent, 12);
        p.accentHover = AdjustColor(p.accent, 12);
        p.accentPressed = AdjustColor(p.accent, -24);
        p.accentText = Rgb(248, 251, 255);
        p.success = Rgb(35, 144, 89);
        p.successSoft = Rgb(226, 244, 235);
        p.warning = Rgb(177, 116, 28);
        p.warningSoft = Rgb(252, 240, 218);
        p.danger = Rgb(194, 74, 53);
        p.dangerSoft = Rgb(252, 233, 228);
        p.shadow = Rgb(192, 200, 212);
        p.taskbar = Rgb(239, 242, 247);
    }
    return p;
}

void DrawRoundRectColor(HDC dc, RECT rect, int radius, COLORREF fill, COLORREF border) {
    HBRUSH brush = CreateSolidBrush(fill);
    HPEN pen = CreatePen(PS_SOLID, 1, border);
    HGDIOBJ oldBrush = SelectObject(dc, brush);
    HGDIOBJ oldPen = SelectObject(dc, pen);
    RoundRect(dc, rect.left, rect.top, rect.right, rect.bottom, radius, radius);
    SelectObject(dc, oldPen);
    SelectObject(dc, oldBrush);
    DeleteObject(pen);
    DeleteObject(brush);
}

void DrawRoundRectOutline(HDC dc, RECT rect, int radius, COLORREF color) {
    HPEN pen = CreatePen(PS_SOLID, 1, color);
    HGDIOBJ oldPen = SelectObject(dc, pen);
    HGDIOBJ oldBrush = SelectObject(dc, GetStockObject(NULL_BRUSH));
    RoundRect(dc, rect.left, rect.top, rect.right, rect.bottom, radius, radius);
    SelectObject(dc, oldBrush);
    SelectObject(dc, oldPen);
    DeleteObject(pen);
}

void FillHorizontalGradient(HDC dc, RECT rect, COLORREF from, COLORREF to) {
    TRIVERTEX vertices[2]{};
    vertices[0].x = rect.left;
    vertices[0].y = rect.top;
    vertices[0].Red = static_cast<COLOR16>(GetRValue(from) << 8);
    vertices[0].Green = static_cast<COLOR16>(GetGValue(from) << 8);
    vertices[0].Blue = static_cast<COLOR16>(GetBValue(from) << 8);
    vertices[1].x = rect.right;
    vertices[1].y = rect.bottom;
    vertices[1].Red = static_cast<COLOR16>(GetRValue(to) << 8);
    vertices[1].Green = static_cast<COLOR16>(GetGValue(to) << 8);
    vertices[1].Blue = static_cast<COLOR16>(GetBValue(to) << 8);
    GRADIENT_RECT gradient{0, 1};
    GradientFill(dc, vertices, 2, &gradient, 1, GRADIENT_FILL_RECT_H);
}

void DrawGradientBar(HDC dc, RECT rect, int percent, COLORREF track, COLORREF from, COLORREF to) {
    int radius = std::max(2, static_cast<int>(rect.bottom - rect.top));
    DrawRoundRectColor(dc, rect, radius, track, track);
    if (percent < 0) return;
    int width = rect.right - rect.left;
    int used = (width * std::min(100, std::max(0, percent))) / 100;
    if (used <= 0) return;
    RECT bar = rect;
    bar.right = bar.left + std::max(used, radius);
    HRGN clip = CreateRoundRectRgn(bar.left, bar.top, bar.right + 1, bar.bottom + 1, radius, radius);
    if (clip) {
        SelectClipRgn(dc, clip);
        FillHorizontalGradient(dc, bar, from, to);
        SelectClipRgn(dc, nullptr);
        DeleteObject(clip);
    } else {
        FillHorizontalGradient(dc, bar, from, to);
    }
}

void DrawStatusDot(HDC dc, int centerX, int centerY, int radius, COLORREF fill) {
    HBRUSH brush = CreateSolidBrush(fill);
    HPEN pen = CreatePen(PS_SOLID, 1, fill);
    HGDIOBJ oldBrush = SelectObject(dc, brush);
    HGDIOBJ oldPen = SelectObject(dc, pen);
    Ellipse(dc, centerX - radius, centerY - radius, centerX + radius + 1, centerY + radius + 1);
    SelectObject(dc, oldPen);
    SelectObject(dc, oldBrush);
    DeleteObject(pen);
    DeleteObject(brush);
}

std::wstring PercentText(int percent) {
    return percent >= 0 ? std::to_wstring(std::min(100, std::max(0, percent))) + L"%" : L"--";
}

// Rows store percent USED (app-server "usedPercent"); the UI shows percent LEFT, like CodexBar.
int UsageDisplayPercent(int usedPercent) {
    if (usedPercent < 0) return -1;
    return std::max(0, std::min(100, 100 - usedPercent));
}

COLORREF StatusColor(const UsageRow& row, const FluentPalette& palette) {
    if (row.status == L"OK" || row.status == L"Ready") return palette.success;
    if (row.status == L"Refreshing" || row.status == L"Account OK") return palette.warning;
    if (row.status == L"Needs login" || row.status == L"Error" || row.status == L"CLI missing") return palette.danger;
    return palette.accent;
}

std::vector<UsageRow> SnapshotRows(AppConfig* configOut = nullptr) {
    std::lock_guard<std::mutex> lock(g_app.rowsMutex);
    if (configOut) *configOut = g_app.config;
    return g_app.rows;
}

std::optional<UsageRow> PickPrimaryCodexRow(const std::vector<UsageRow>& rows) {
    for (const auto& row : rows) {
        if (row.provider == L"Codex" && row.primaryPercent >= 0) return row;
    }
    for (const auto& row : rows) {
        if (row.provider == L"Codex") return row;
    }
    if (!rows.empty()) return rows.front();
    return std::nullopt;
}

HWND FindDescendantWindowByClass(HWND root, const wchar_t* className) {
    struct Search {
        const wchar_t* className = nullptr;
        HWND found = nullptr;
    } search{className, nullptr};

    EnumChildWindows(root, [](HWND hwnd, LPARAM lParam) -> BOOL {
        auto* search = reinterpret_cast<Search*>(lParam);
        wchar_t name[128]{};
        GetClassNameW(hwnd, name, ARRAYSIZE(name));
        if (_wcsicmp(name, search->className) == 0) {
            search->found = hwnd;
            return FALSE;
        }
        return TRUE;
    }, reinterpret_cast<LPARAM>(&search));

    return search.found;
}

HWND FindPrimaryTaskbarWindow() {
    return FindWindowW(L"Shell_TrayWnd", nullptr);
}

bool IsUsableTaskbarWindow(HWND hwnd) {
    if (!hwnd || !IsWindow(hwnd) || !IsWindowVisible(hwnd)) return false;
    RECT rect{};
    return GetWindowRect(hwnd, &rect) && IsMeaningfulRect(rect);
}

std::vector<HWND> FindTaskbarWindows() {
    std::vector<HWND> taskbars;
    HWND primary = FindPrimaryTaskbarWindow();
    if (IsUsableTaskbarWindow(primary)) taskbars.push_back(primary);

    HWND previous = nullptr;
    while (true) {
        HWND secondary = FindWindowExW(nullptr, previous, L"Shell_SecondaryTrayWnd", nullptr);
        if (!secondary) break;
        if (IsUsableTaskbarWindow(secondary)) taskbars.push_back(secondary);
        previous = secondary;
    }
    if (taskbars.empty() && primary && IsWindow(primary)) taskbars.push_back(primary);
    return taskbars;
}

bool RectContainsPoint(const RECT& rect, POINT point) {
    return point.x >= rect.left && point.x < rect.right && point.y >= rect.top && point.y < rect.bottom;
}

HWND FindTaskbarForPoint(POINT point) {
    std::vector<HWND> taskbars = FindTaskbarWindows();
    for (HWND taskbar : taskbars) {
        RECT rect{};
        if (GetWindowRect(taskbar, &rect) && RectContainsPoint(rect, point)) {
            return taskbar;
        }
    }

    HMONITOR pointMonitor = MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
    for (HWND taskbar : taskbars) {
        RECT rect{};
        if (GetWindowRect(taskbar, &rect) && MonitorFromRect(&rect, MONITOR_DEFAULTTONEAREST) == pointMonitor) {
            return taskbar;
        }
    }
    return taskbars.empty() ? nullptr : taskbars.front();
}

HWND FindTrayNotifyWindow(HWND taskbar) {
    if (!taskbar) return nullptr;
    HWND direct = FindWindowExW(taskbar, nullptr, L"TrayNotifyWnd", nullptr);
    return direct ? direct : FindDescendantWindowByClass(taskbar, L"TrayNotifyWnd");
}

HWND FindTaskListWindow(HWND taskbar) {
    if (!taskbar) return nullptr;
    HWND taskList = FindDescendantWindowByClass(taskbar, L"MSTaskListWClass");
    if (taskList) return taskList;
    return FindDescendantWindowByClass(taskbar, L"MSTaskSwWClass");
}

int RectWidth(const RECT& rect) {
    return static_cast<int>(rect.right - rect.left);
}

int RectHeight(const RECT& rect) {
    return static_cast<int>(rect.bottom - rect.top);
}

std::wstring RectToText(const RECT& rect) {
    return L"[" + std::to_wstring(rect.left) + L"," + std::to_wstring(rect.top) + L"," +
           std::to_wstring(rect.right) + L"," + std::to_wstring(rect.bottom) + L"]";
}

void WriteTaskbarPlacementLog(const std::wstring& text) {
    static std::wstring lastLog;
    if (text == lastLog) return;
    lastLog = text;
    WriteTextFileUtf8(ConfigDir() + L"\\last-taskbar-placement.log", WideToUtf8(text));
}

RECT ClampRectToWorkArea(RECT rect, const RECT& workArea) {
    int width = rect.right - rect.left;
    int height = rect.bottom - rect.top;
    if (rect.left < workArea.left) {
        rect.left = workArea.left;
        rect.right = rect.left + width;
    }
    if (rect.right > workArea.right) {
        rect.right = workArea.right;
        rect.left = rect.right - width;
    }
    if (rect.top < workArea.top) {
        rect.top = workArea.top;
        rect.bottom = rect.top + height;
    }
    if (rect.bottom > workArea.bottom) {
        rect.bottom = workArea.bottom;
        rect.top = rect.bottom - height;
    }
    return rect;
}

void UpdatePresenceRegion(HWND hwnd, const RECT& widgetClientRect, UINT dpi) {
    int radius = ScaleForDpi(9, dpi);
    HRGN region = CreateRoundRectRgn(
        widgetClientRect.left,
        widgetClientRect.top,
        widgetClientRect.right + 1,
        widgetClientRect.bottom + 1,
        radius,
        radius
    );
    if (region && SetWindowRgn(hwnd, region, TRUE) == 0) {
        DeleteObject(region);
    }
}

COLORREF WidgetKeyColor(bool dark) {
    return dark ? Rgb(20, 20, 22) : Rgb(240, 242, 245);
}

void PositionTaskbarPresenceWindow(HWND hwnd, HWND taskbar) {
    if (!hwnd || !IsWindow(hwnd)) return;
    TaskbarPresenceState* state = GetTaskbarPresenceState(hwnd);
    if (!taskbar || !IsWindow(taskbar)) {
        ShowWindow(hwnd, SW_HIDE);
        WriteTaskbarPlacementLog(L"Taskbar widget hidden: no valid Explorer taskbar window.");
        return;
    }
    if (state) state->taskbar = taskbar;

    LONG_PTR style = GetWindowLongPtrW(hwnd, GWL_STYLE);
    style &= ~static_cast<LONG_PTR>(WS_CHILD);
    style |= WS_POPUP | WS_CLIPSIBLINGS | WS_CLIPCHILDREN;
    SetWindowLongPtrW(hwnd, GWL_STYLE, style);

    LONG_PTR exStyle = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
    exStyle &= ~static_cast<LONG_PTR>(WS_EX_APPWINDOW);
    exStyle &= ~static_cast<LONG_PTR>(WS_EX_TOPMOST);
    exStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_LAYERED;
    SetWindowLongPtrW(hwnd, GWL_EXSTYLE, exStyle);

    SetWindowLongPtrW(hwnd, GWLP_HWNDPARENT, reinterpret_cast<LONG_PTR>(taskbar));
    g_app.taskbarParentWindow = taskbar;

    RECT taskbarRect{};
    if (!GetWindowRect(taskbar, &taskbarRect)) return;

    RECT trayRect{};
    HWND tray = FindTrayNotifyWindow(taskbar);
    bool hasTrayRect = tray && GetWindowRect(tray, &trayRect);
    RECT taskListRect{};
    HWND taskList = FindTaskListWindow(taskbar);
    bool hasTaskListRect = taskList && GetWindowRect(taskList, &taskListRect);

    UINT dpi = GetDpiForHwnd(hwnd);
    UiScale S{dpi};
    int taskbarWidth = std::max(1, RectWidth(taskbarRect));
    int taskbarHeight = std::max(1, RectHeight(taskbarRect));
    bool vertical = taskbarHeight > taskbarWidth;

    int width = vertical ? std::max(S(52), taskbarWidth - S(8)) : S(304);
    int height = vertical ? S(184) : std::max(S(40), std::min(S(48), taskbarHeight - S(4)));
    int x = taskbarRect.left + S(8);
    int y = taskbarRect.top + S(4);
    bool compact = false;
    bool overlayingTaskList = false;
    std::wstring note = L"normal";

    if (!vertical) {
        int rightLimit = hasTrayRect ? trayRect.left - S(8) : taskbarRect.right - S(18);
        int leftLimit = taskbarRect.left + S(8);
        int available = std::max(0, rightLimit - leftLimit);
        if (available < width) {
            width = std::max(S(64), std::min(width, available));
            compact = width < S(190);
            note = compact ? L"compact: limited space before tray" : L"narrow: limited space before tray";
        }
        if (available < S(52)) {
            width = S(72);
            rightLimit = std::min(static_cast<int>(taskbarRect.right) - S(8), rightLimit + S(18));
            note = L"icon-only fallback: taskbar space is very small";
            compact = true;
        }
        x = rightLimit - width;
        x = std::max(leftLimit, std::min(x, static_cast<int>(taskbarRect.right) - width - S(6)));
        y = taskbarRect.top + (taskbarHeight - height) / 2;
        if (hasTaskListRect && x < taskListRect.right && x + width > taskListRect.left) {
            overlayingTaskList = true;
            note += L"; overlaying task list because Explorer exposes no reserve API";
        }
    } else {
        int trayTop = hasTrayRect ? trayRect.top : taskbarRect.bottom - S(180);
        int available = std::max(0, trayTop - static_cast<int>(taskbarRect.top) - S(16));
        if (available < height) {
            height = std::max(S(72), std::min(height, available));
            compact = height < S(128);
            note = compact ? L"compact vertical: limited space before tray" : L"narrow vertical: limited space before tray";
        }
        x = taskbarRect.left + (taskbarWidth - width) / 2;
        y = trayTop - height - S(8);
        y = std::max(static_cast<int>(taskbarRect.top) + S(8), std::min(y, static_cast<int>(taskbarRect.bottom) - height - S(8)));
    }

    RECT widgetRect{x, y, x + width, y + height};
    SetWindowPos(
        hwnd,
        HWND_TOP,
        widgetRect.left,
        widgetRect.top,
        width,
        height,
        SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_FRAMECHANGED
    );
    UINT settledDpi = GetDpiForHwnd(hwnd);
    if (settledDpi != dpi) {
        PositionTaskbarPresenceWindow(hwnd, taskbar);
        return;
    }

    RECT widgetClientRect{0, 0, width, height};
    if (state) {
        state->hostScreenRect = widgetRect;
        state->widgetClientRect = widgetClientRect;
        state->widgetScreenRect = widgetRect;
        state->compact = compact;
        state->vertical = vertical;
    }
    if (hwnd == g_app.taskbarPresenceWindow || g_app.taskbarPresenceScreenRect.right <= g_app.taskbarPresenceScreenRect.left) {
        g_app.taskbarPresenceScreenRect = widgetRect;
    }

    UpdatePresenceRegion(hwnd, widgetClientRect, dpi);
    SetLayeredWindowAttributes(hwnd, WidgetKeyColor(IsWindowsDarkMode()), 0, LWA_COLORKEY);
    SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    InvalidateRect(hwnd, nullptr, FALSE);

    std::wstring log = L"Codex SWBar Windows taskbar widget placement\n";
    log += L"taskbar=" + RectToText(taskbarRect) + L" class=" + std::wstring(vertical ? L"vertical" : L"horizontal") + L" dpi=" + std::to_wstring(dpi) + L"\n";
    log += L"tray=" + std::wstring(hasTrayRect ? RectToText(trayRect) : L"<missing>") + L"\n";
    log += L"taskList=" + std::wstring(hasTaskListRect ? RectToText(taskListRect) : L"<missing>") + L"\n";
    log += L"host=" + RectToText(widgetRect) + L" clientWidget=" + RectToText(widgetClientRect) + L"\n";
    log += L"widget=" + RectToText(widgetRect) + L" compact=" + std::wstring(compact ? L"true" : L"false") + L"\n";
    log += L"note=" + note + (overlayingTaskList ? L" (expected for Windows taskbars without reserved widget slots)" : L"") + L"\n";
    WriteTaskbarPlacementLog(log);
}

void PositionTaskbarPresence() {
    for (HWND hwnd : g_app.taskbarPresenceWindows) {
        if (!hwnd || !IsWindow(hwnd)) continue;
        TaskbarPresenceState* state = GetTaskbarPresenceState(hwnd);
        PositionTaskbarPresenceWindow(hwnd, state ? state->taskbar : nullptr);
    }
}

void UpdateTaskbarPresence() {
    PositionTaskbarPresence();
    for (HWND hwnd : g_app.taskbarPresenceWindows) {
        if (hwnd && IsWindow(hwnd)) {
            InvalidateRect(hwnd, nullptr, FALSE);
        }
    }
    if (g_app.codexBarFlyoutWindow && IsWindowVisible(g_app.codexBarFlyoutWindow)) {
        InvalidateRect(g_app.codexBarFlyoutWindow, nullptr, FALSE);
    }
}

HWND FindPresenceWindowForTaskbar(HWND taskbar) {
    for (HWND hwnd : g_app.taskbarPresenceWindows) {
        if (!hwnd || !IsWindow(hwnd)) continue;
        TaskbarPresenceState* state = GetTaskbarPresenceState(hwnd);
        if (state && state->taskbar == taskbar) return hwnd;
    }
    return nullptr;
}

bool TaskbarPresenceMatchesTaskbars(const std::vector<HWND>& taskbars) {
    if (g_app.taskbarPresenceWindows.size() != taskbars.size()) return false;

    for (HWND hwnd : g_app.taskbarPresenceWindows) {
        if (!hwnd || !IsWindow(hwnd)) return false;
        TaskbarPresenceState* state = GetTaskbarPresenceState(hwnd);
        if (!state || !IsUsableTaskbarWindow(state->taskbar)) return false;

        bool found = false;
        for (HWND taskbar : taskbars) {
            if (taskbar == state->taskbar) {
                found = true;
                break;
            }
        }
        if (!found) return false;
    }

    for (HWND taskbar : taskbars) {
        if (!FindPresenceWindowForTaskbar(taskbar)) return false;
    }
    return true;
}

void EnsureTaskbarPresenceTopology(HWND owner) {
    std::vector<HWND> taskbars = FindTaskbarWindows();
    if (TaskbarPresenceMatchesTaskbars(taskbars)) {
        PositionTaskbarPresence();
        return;
    }

    bool reopenFlyout = g_app.codexBarFlyoutWindow &&
                        IsWindow(g_app.codexBarFlyoutWindow) &&
                        IsWindowVisible(g_app.codexBarFlyoutWindow);
    RecreateTaskbarPresence(owner);
    if (reopenFlyout) ShowCodexBarFlyout(nullptr);
}

bool ActivateTaskbarPresenceWindow(HWND hwnd) {
    if (!hwnd || !IsWindow(hwnd)) return false;
    TaskbarPresenceState* state = GetTaskbarPresenceState(hwnd);
    if (!state || state->widgetScreenRect.right <= state->widgetScreenRect.left ||
        state->widgetScreenRect.bottom <= state->widgetScreenRect.top) {
        return false;
    }
    g_app.taskbarPresenceWindow = hwnd;
    g_app.taskbarParentWindow = state->taskbar;
    g_app.taskbarPresenceScreenRect = state->widgetScreenRect;
    return true;
}

bool ActivateTaskbarPresenceForTaskbar(HWND taskbar) {
    HWND hwnd = FindPresenceWindowForTaskbar(taskbar);
    if (ActivateTaskbarPresenceWindow(hwnd)) return true;
    if (taskbar && IsWindow(taskbar)) {
        g_app.taskbarParentWindow = taskbar;
    }
    return false;
}

void UpdateTaskbarAnchorFromCursor() {
    POINT cursor{};
    GetCursorPos(&cursor);
    HWND taskbar = FindTaskbarForPoint(cursor);
    if (ActivateTaskbarPresenceForTaskbar(taskbar)) return;

    if (taskbar && IsWindow(taskbar)) {
        g_app.taskbarParentWindow = taskbar;
    }
    g_app.taskbarPresenceScreenRect = RECT{cursor.x, cursor.y, cursor.x + 1, cursor.y + 1};
}

void TouchCodexBarFlyout() {
    ULONGLONG now = NowTickMs();
    if (g_app.flyoutOpenedTick == 0) g_app.flyoutOpenedTick = now;
    g_app.flyoutLastInteractionTick = now;
}

bool PointInWindowScreen(HWND hwnd, POINT point) {
    if (!hwnd || !IsWindow(hwnd) || !IsWindowVisible(hwnd)) return false;
    RECT rect{};
    return GetWindowRect(hwnd, &rect) && RectContainsPoint(rect, point);
}

bool PointInAnyPresenceWidget(POINT point) {
    for (HWND hwnd : g_app.taskbarPresenceWindows) {
        if (!hwnd || !IsWindowVisible(hwnd)) continue;
        TaskbarPresenceState* state = GetTaskbarPresenceState(hwnd);
        if (state && RectContainsPoint(state->widgetScreenRect, point)) return true;
    }
    return false;
}

bool AnyMouseButtonDown() {
    return (GetAsyncKeyState(VK_LBUTTON) & 0x8000) ||
           (GetAsyncKeyState(VK_RBUTTON) & 0x8000) ||
           (GetAsyncKeyState(VK_MBUTTON) & 0x8000) ||
           (GetAsyncKeyState(VK_XBUTTON1) & 0x8000) ||
           (GetAsyncKeyState(VK_XBUTTON2) & 0x8000);
}

void UpdateCodexBarFlyoutWatchdog() {
    HWND hwnd = g_app.codexBarFlyoutWindow;
    if (!hwnd || !IsWindow(hwnd) || !IsWindowVisible(hwnd)) {
        if (g_app.mainWindow) KillTimer(g_app.mainWindow, TIMER_FLYOUT_WATCHDOG);
        return;
    }

    ULONGLONG now = NowTickMs();
    POINT cursor{};
    GetCursorPos(&cursor);
    bool insideFlyout = PointInWindowScreen(hwnd, cursor);
    bool insideWidget = PointInAnyPresenceWidget(cursor);
    if (insideFlyout || insideWidget) {
        TouchCodexBarFlyout();
        return;
    }

    if (AnyMouseButtonDown() && now - g_app.flyoutOpenedTick > FLYOUT_OUTSIDE_CLICK_GRACE_MS) {
        HideCodexBarFlyout();
        return;
    }

    if (g_app.flyoutLastInteractionTick != 0 && now - g_app.flyoutLastInteractionTick > FLYOUT_AUTO_HIDE_MS) {
        HideCodexBarFlyout();
    }
}

struct FlyoutGroupInfo {
    std::wstring provider;
    int rowCount = 0;
};

std::vector<FlyoutGroupInfo> FlyoutGroupCounts(const std::vector<UsageRow>& rows) {
    std::vector<FlyoutGroupInfo> groups;
    for (const auto& row : rows) {
        std::wstring provider = row.provider.empty() ? L"Codex" : row.provider;
        bool found = false;
        for (auto& group : groups) {
            if (_wcsicmp(group.provider.c_str(), provider.c_str()) == 0) {
                group.rowCount = std::min(4, group.rowCount + 1);
                found = true;
                break;
            }
        }
        if (!found && groups.size() < 2) groups.push_back(FlyoutGroupInfo{provider, 1});
    }
    return groups;
}

SIZE CodexBarFlyoutSize(UINT dpi) {
    UiScale S{dpi};
    std::vector<FlyoutGroupInfo> groups = FlyoutGroupCounts(SnapshotRows());
    int height = S(52);
    if (groups.empty()) {
        height += S(8) + S(76);
    } else {
        for (const auto& group : groups) {
            height += S(8) + S(56) + group.rowCount * S(64);
        }
    }
    height += S(10) + S(44);
    height += S(64);
    return SIZE{S(400), height};
}

void PositionCodexBarFlyout(HWND hwnd) {
    if (!hwnd || !IsWindow(hwnd)) return;

    RECT anchor = g_app.taskbarPresenceScreenRect;
    if (anchor.right <= anchor.left || anchor.bottom <= anchor.top) {
        UpdateTaskbarAnchorFromCursor();
        anchor = g_app.taskbarPresenceScreenRect;
        if (anchor.right <= anchor.left || anchor.bottom <= anchor.top) {
            POINT cursor{};
            GetCursorPos(&cursor);
            anchor = RECT{cursor.x, cursor.y, cursor.x + 1, cursor.y + 1};
        }
    }

    HMONITOR monitor = MonitorFromRect(&anchor, MONITOR_DEFAULTTONEAREST);
    MONITORINFO mi{};
    mi.cbSize = sizeof(mi);
    GetMonitorInfoW(monitor, &mi);
    RECT workArea = IsMeaningfulRect(mi.rcWork) ? mi.rcWork : mi.rcMonitor;
    UINT dpi = GetDpiForHwnd(hwnd);
    UiScale S{dpi};
    SIZE size = CodexBarFlyoutSize(dpi);

    int monitorMidY = mi.rcMonitor.top + (mi.rcMonitor.bottom - mi.rcMonitor.top) / 2;
    int monitorMidX = mi.rcMonitor.left + (mi.rcMonitor.right - mi.rcMonitor.left) / 2;
    bool anchorOnBottom = anchor.top >= monitorMidY;
    bool anchorIsVertical = RectHeight(anchor) > RectWidth(anchor);
    RECT target{};
    if (anchorIsVertical) {
        bool anchorOnLeft = anchor.left < monitorMidX;
        if (anchorOnLeft) {
            target.left = anchor.right + S(10);
            target.right = target.left + size.cx;
        } else {
            target.right = anchor.left - S(10);
            target.left = target.right - size.cx;
        }
        target.top = anchor.top;
        target.bottom = target.top + size.cy;
    } else {
        target.left = anchor.right - size.cx;
        target.right = target.left + size.cx;
        if (anchorOnBottom) {
            target.bottom = anchor.top - S(10);
            target.top = target.bottom - size.cy;
        } else {
            target.top = anchor.bottom + S(10);
            target.bottom = target.top + size.cy;
        }
    }
    target = ClampRectToWorkArea(target, workArea);

    UINT flags = SWP_NOACTIVATE;
    if (IsWindowVisible(hwnd)) flags |= SWP_SHOWWINDOW;
    SetWindowPos(
        hwnd,
        HWND_TOPMOST,
        target.left,
        target.top,
        size.cx,
        size.cy,
        flags
    );
    UINT settledDpi = GetDpiForHwnd(hwnd);
    if (settledDpi != dpi) {
        PositionCodexBarFlyout(hwnd);
        return;
    }
    std::wstring log = L"Codex SWBar Windows flyout placement\n";
    log += L"anchor=" + RectToText(anchor) + L"\n";
    log += L"target=" + RectToText(target) + L"\n";
    log += L"size=" + std::to_wstring(size.cx) + L"x" + std::to_wstring(size.cy) + L" dpi=" + std::to_wstring(dpi) + L" settledDpi=" + std::to_wstring(settledDpi) + L"\n";
    log += L"workArea=" + RectToText(workArea) + L"\n";
    WriteTextFileUtf8(ConfigDir() + L"\\last-flyout-placement.log", WideToUtf8(log));
}

COLORREF FlyoutBaseColor(bool dark) {
    return dark ? Rgb(32, 32, 36) : Rgb(243, 243, 243);
}

void ConfigureFlyoutMaterial(HWND hwnd) {
    if (!hwnd || !IsWindow(hwnd)) return;
    bool dark = IsWindowsDarkMode();
    BOOL darkAttr = dark ? TRUE : FALSE;
    TrySetDwmAttribute(hwnd, 20, &darkAttr, sizeof(darkAttr));
    TrySetDwmAttribute(hwnd, 19, &darkAttr, sizeof(darkAttr));
    DWORD roundCorners = 2;
    TrySetDwmAttribute(hwnd, 33, &roundCorners, sizeof(roundCorners));

    COLORREF base = FlyoutBaseColor(dark);
    bool wantAcrylic = CurrentConfigSnapshot().flyoutStyle != L"solid";
    bool acrylic = wantAcrylic && TryApplyAcrylicAccent(hwnd, base, dark ? 218 : 232);
    if (!wantAcrylic) TryApplyAcrylicAccent(hwnd, base, 0, false);
    if (acrylic) {
        SetLayeredWindowAttributes(hwnd, base, 0, LWA_COLORKEY);
    } else {
        SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
    }
    g_app.flyoutAcrylicActive = acrylic;
}

HWND EnsureCodexBarFlyout() {
    if (g_app.codexBarFlyoutWindow && IsWindow(g_app.codexBarFlyoutWindow)) {
        return g_app.codexBarFlyoutWindow;
    }

    HWND hwnd = CreateWindowExW(
        WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_LAYERED | WS_EX_NOACTIVATE,
        kCodexBarFlyoutClass,
        L"CodexBar",
        WS_POPUP,
        CW_USEDEFAULT,
        CW_USEDEFAULT,
        1,
        1,
        g_app.mainWindow,
        nullptr,
        g_app.instance,
        nullptr
    );
    g_app.codexBarFlyoutWindow = hwnd;
    ConfigureFlyoutMaterial(hwnd);
    return hwnd;
}

void HideCodexBarFlyout() {
    if (g_app.codexBarFlyoutWindow && IsWindow(g_app.codexBarFlyoutWindow)) {
        ShowWindow(g_app.codexBarFlyoutWindow, SW_HIDE);
    }
    if (g_app.mainWindow) KillTimer(g_app.mainWindow, TIMER_FLYOUT_WATCHDOG);
    g_app.flyoutHoverTarget = TargetKey{};
    g_app.flyoutPressedTarget = TargetKey{};
    g_app.flyoutTrackingMouseLeave = false;
    g_app.flyoutOpenedTick = 0;
    g_app.flyoutLastInteractionTick = 0;
}

void ShowCodexBarFlyout(HWND sourcePresence) {
    if (sourcePresence && IsWindow(sourcePresence)) {
        ActivateTaskbarPresenceWindow(sourcePresence);
    } else {
        UpdateTaskbarAnchorFromCursor();
    }

    HWND hwnd = EnsureCodexBarFlyout();
    if (!hwnd) return;
    PositionCodexBarFlyout(hwnd);
    ConfigureFlyoutMaterial(hwnd);
    g_app.flyoutOpenedTick = NowTickMs();
    g_app.flyoutLastInteractionTick = g_app.flyoutOpenedTick;
    ShowWindow(hwnd, SW_SHOWNOACTIVATE);
    if (g_app.mainWindow) SetTimer(g_app.mainWindow, TIMER_FLYOUT_WATCHDOG, 120u, nullptr);
    InvalidateRect(hwnd, nullptr, FALSE);
}

void ToggleCodexBarFlyout(HWND sourcePresence) {
    HWND hwnd = EnsureCodexBarFlyout();
    if (!hwnd) return;
    if (IsWindowVisible(hwnd)) {
        HideCodexBarFlyout();
    } else {
        ShowCodexBarFlyout(sourcePresence);
    }
}

void DestroyTaskbarPresence() {
    HideCodexBarFlyout();
    for (HWND hwnd : g_app.taskbarPresenceWindows) {
        if (hwnd && IsWindow(hwnd)) {
            DestroyWindow(hwnd);
        }
    }
    g_app.taskbarPresenceWindows.clear();
    g_app.taskbarPresenceWindow = nullptr;
    g_app.taskbarParentWindow = nullptr;
    g_app.taskbarPresenceHover = false;
    g_app.taskbarPresencePressed = false;
    g_app.taskbarPresenceTrackingMouseLeave = false;
}

void RecreateTaskbarPresence(HWND owner) {
    DestroyTaskbarPresence();
    std::vector<HWND> taskbars = FindTaskbarWindows();
    for (HWND taskbar : taskbars) {
        HWND hwnd = CreateWindowExW(
            WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED,
            kTaskbarPresenceClass,
            L"Codex",
            WS_POPUP | WS_CLIPSIBLINGS | WS_CLIPCHILDREN,
            0,
            0,
            1,
            1,
            taskbar,
            nullptr,
            g_app.instance,
            nullptr
        );
        if (!hwnd) {
            hwnd = CreateWindowExW(
                WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED,
                kTaskbarPresenceClass,
                L"Codex",
                WS_POPUP,
                0,
                0,
                1,
                1,
                owner,
                nullptr,
                g_app.instance,
                nullptr
            );
        }
        if (!hwnd) continue;
        auto* state = new TaskbarPresenceState();
        state->taskbar = taskbar;
        SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(state));
        g_app.taskbarPresenceWindows.push_back(hwnd);
        if (!g_app.taskbarPresenceWindow) {
            g_app.taskbarPresenceWindow = hwnd;
            g_app.taskbarParentWindow = taskbar;
        }
    }
    PositionTaskbarPresence();
}

void PaintTaskbarPresence(HWND hwnd) {
    PAINTSTRUCT ps{};
    HDC windowDc = BeginPaint(hwnd, &ps);
    RECT client{};
    GetClientRect(hwnd, &client);
    int width = std::max(1, static_cast<int>(client.right - client.left));
    int height = std::max(1, static_cast<int>(client.bottom - client.top));

    HDC bufferDc = CreateCompatibleDC(windowDc);
    HBITMAP bitmap = bufferDc ? CreateCompatibleBitmap(windowDc, width, height) : nullptr;
    HGDIOBJ oldBitmap = nullptr;
    HDC dc = windowDc;
    if (bufferDc && bitmap) {
        oldBitmap = SelectObject(bufferDc, bitmap);
        dc = bufferDc;
    }

    UINT dpi = GetDpiForHwnd(hwnd);
    UiScale S{dpi};
    bool dark = IsWindowsDarkMode();
    FluentPalette palette = CurrentPalette();
    COLORREF keyColor = WidgetKeyColor(dark);
    COLORREF ink = palette.text;
    COLORREF inkSecondary = palette.muted;
    COLORREF track = dark ? Rgb(82, 84, 92) : Rgb(196, 200, 208);
    COLORREF barFrom = dark ? Rgb(138, 99, 255) : Rgb(116, 77, 233);
    COLORREF barTo = dark ? Rgb(82, 170, 255) : Rgb(40, 130, 220);
    FillRectColor(dc, client, keyColor);

    TaskbarPresenceState* state = GetTaskbarPresenceState(hwnd);
    RECT widget = state && state->widgetClientRect.right > state->widgetClientRect.left
        ? state->widgetClientRect
        : client;

    RECT surface = widget;
    InflateRect(&surface, -S(1), -S(1));
    if (g_app.taskbarPresencePressed || g_app.taskbarPresenceHover) {
        COLORREF surfaceFill = g_app.taskbarPresencePressed
            ? (dark ? Rgb(48, 48, 54) : Rgb(228, 230, 236))
            : (dark ? Rgb(54, 54, 60) : Rgb(233, 235, 241));
        DrawRoundRectColor(dc, surface, S(8), surfaceFill, surfaceFill);
    }

    HFONT titleFont = CreateFontW(S(18), 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                                  OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                                  DEFAULT_PITCH, L"Segoe UI Variable");
    HFONT dataFont = CreateFontW(S(20), 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                                 OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                                 DEFAULT_PITCH, L"Segoe UI Variable");
    HFONT smallFont = CreateFontW(S(14), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                                  OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                                  DEFAULT_PITCH, L"Segoe UI");

    std::vector<UsageRow> rows = SnapshotRows();
    auto picked = PickPrimaryCodexRow(rows);
    std::wstring title = L"Codex";
    std::wstring detail = g_app.refreshing ? L"Syncing" : L"Waiting";
    std::wstring percent = L"--";
    COLORREF stateColor = palette.warning;
    int primaryPercent = -1;
    int secondaryPercent = -1;
    if (picked) {
        title = picked->label.empty() ? picked->provider : picked->label;
        primaryPercent = UsageDisplayPercent(picked->primaryPercent);
        secondaryPercent = UsageDisplayPercent(picked->secondaryPercent);
        int leadingPercent = primaryPercent >= 0 ? primaryPercent : secondaryPercent;
        percent = PercentText(leadingPercent);
        stateColor = StatusColor(*picked, palette);
        if (primaryPercent >= 0 && secondaryPercent >= 0) {
            detail = L"wk " + PercentText(secondaryPercent);
        } else if (primaryPercent >= 0 || secondaryPercent >= 0) {
            detail = picked->plan.empty() ? picked->provider : picked->plan;
        } else {
            detail = picked->status.empty() ? picked->provider : picked->status;
        }
    }

    int widgetWidth = std::max(1, RectWidth(surface));
    int widgetHeight = std::max(1, RectHeight(surface));
    bool compact = widgetWidth < S(156) || (state && state->compact);
    int iconSize = std::max(S(26), std::min(S(36), widgetHeight - S(10)));
    int iconX = surface.left + S(8);
    int iconY = surface.top + (widgetHeight - iconSize) / 2;
    if (g_app.smallIcon) {
        DrawIconEx(dc, iconX, iconY, g_app.smallIcon, iconSize, iconSize, 0, nullptr, DI_NORMAL);
    } else {
        RECT iconBox{iconX, iconY, iconX + iconSize, iconY + iconSize};
        DrawRoundRectColor(dc, iconBox, S(7), palette.surfaceAlt, track);
    }
    DrawStatusDot(dc, iconX + iconSize - S(2), iconY + iconSize - S(2), S(3), stateColor);

    int textLeft = iconX + iconSize + S(10);
    if (compact) {
        RECT percentRect{textLeft, surface.top, surface.right - S(8), surface.bottom};
        DrawTextLine(dc, percent, percentRect, ink, dataFont, DT_RIGHT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS);
    } else {
        int percentWidth = S(66);
        int textRight = surface.right - percentWidth - S(14);
        RECT titleRect{textLeft, surface.top + S(2), textRight, surface.top + S(26)};
        DrawTextLine(dc, title, titleRect, ink, titleFont, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS);
        RECT detailRect{textLeft, surface.top + S(25), textRight, surface.bottom - S(8)};
        DrawTextLine(dc, detail, detailRect, inkSecondary, smallFont, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS);
        RECT percentRect{surface.right - percentWidth - S(10), surface.top, surface.right - S(10), surface.bottom};
        DrawTextLine(dc, percent, percentRect, picked && (primaryPercent >= 0 || secondaryPercent >= 0) ? ink : inkSecondary,
                     dataFont, DT_RIGHT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS);
    }

    RECT meter{textLeft, surface.bottom - S(7), surface.right - S(12), surface.bottom - S(3)};
    DrawGradientBar(dc, meter, primaryPercent >= 0 ? primaryPercent : secondaryPercent, track, barFrom, barTo);

    DeleteObject(titleFont);
    DeleteObject(dataFont);
    DeleteObject(smallFont);

    if (dc != windowDc) {
        BitBlt(windowDc, 0, 0, width, height, dc, 0, 0, SRCCOPY);
    }
    if (oldBitmap) SelectObject(bufferDc, oldBitmap);
    if (bitmap) DeleteObject(bitmap);
    if (bufferDc) DeleteDC(bufferDc);
    EndPaint(hwnd, &ps);
}

struct FlyoutActionRects {
    RECT refresh{};
    RECT settings{};
    RECT openConfig{};
};

FlyoutActionRects BuildFlyoutActionRects(HWND hwnd) {
    RECT client{};
    GetClientRect(hwnd, &client);
    UiScale S{GetDpiForHwnd(hwnd)};
    int bottom = client.bottom - S(13);
    int top = bottom - S(38);
    int x = client.left + S(16);
    RECT refresh{x, top, x + S(122), bottom};
    RECT openConfig{refresh.right + S(8), top, refresh.right + S(8) + S(108), bottom};
    RECT settings{client.right - S(16) - S(38), top, client.right - S(16), bottom};
    return FlyoutActionRects{refresh, settings, openConfig};
}

void PaintCodexBarFlyout(HWND hwnd) {
    PAINTSTRUCT ps{};
    HDC windowDc = BeginPaint(hwnd, &ps);
    RECT client{};
    GetClientRect(hwnd, &client);
    int width = std::max(1, static_cast<int>(client.right - client.left));
    int height = std::max(1, static_cast<int>(client.bottom - client.top));

    HDC bufferDc = CreateCompatibleDC(windowDc);
    HBITMAP bitmap = bufferDc ? CreateCompatibleBitmap(windowDc, width, height) : nullptr;
    HGDIOBJ oldBitmap = nullptr;
    HDC dc = windowDc;
    if (bufferDc && bitmap) {
        oldBitmap = SelectObject(bufferDc, bitmap);
        dc = bufferDc;
    }

    UINT dpi = GetDpiForHwnd(hwnd);
    UiScale S{dpi};
    bool dark = IsWindowsDarkMode();
    FluentPalette palette = CurrentPalette();
    COLORREF base = FlyoutBaseColor(dark);
    COLORREF ink = palette.text;
    COLORREF inkSecondary = palette.muted;
    COLORREF cardFill = palette.surface;
    COLORREF cardBorder = palette.border;
    COLORREF hairline = palette.border;
    COLORREF panelBorder = palette.borderStrong;
    COLORREF track = BlendColor(cardFill, palette.text, 16);
    COLORREF codexFrom = dark ? Rgb(138, 99, 255) : Rgb(116, 77, 233);
    COLORREF codexTo = dark ? Rgb(82, 170, 255) : Rgb(40, 130, 220);
    COLORREF claudeFrom = dark ? Rgb(222, 116, 72) : Rgb(196, 90, 50);
    COLORREF claudeTo = dark ? Rgb(255, 170, 110) : Rgb(235, 150, 90);

    FillRectColor(dc, client, base);
    DrawRoundRectOutline(dc, client, S(9), panelBorder);

    HFONT titleFont = CreateFontW(S(21), 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                                  OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                                  DEFAULT_PITCH, L"Segoe UI Variable");
    HFONT sectionFont = CreateFontW(S(19), 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                                    OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                                    DEFAULT_PITCH, L"Segoe UI Variable");
    HFONT bodyFont = CreateFontW(S(18), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                                 OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                                 DEFAULT_PITCH, L"Segoe UI Variable");
    HFONT percentFont = CreateFontW(S(20), 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                                    OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                                    DEFAULT_PITCH, L"Segoe UI Variable");
    HFONT captionFont = CreateFontW(S(15), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                                    OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                                    DEFAULT_PITCH, L"Segoe UI");
    HFONT badgeFont = CreateFontW(S(15), 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                                  OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                                  DEFAULT_PITCH, L"Segoe UI Variable");
    HFONT iconFont = CreateFontW(S(16), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                                 OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                                 DEFAULT_PITCH, L"Segoe Fluent Icons");

    int padX = S(16);
    if (g_app.smallIcon) {
        DrawIconEx(dc, padX, S(12), g_app.smallIcon, S(28), S(28), 0, nullptr, DI_NORMAL);
    }
    RECT titleRect{padX + (g_app.smallIcon ? S(38) : 0), S(9), client.right - S(120), S(43)};
    DrawTextLine(dc, L"CodexBar", titleRect, ink, titleFont, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS);
    if (g_app.refreshing) {
        RECT syncRect{client.right - S(120), S(9), client.right - padX, S(43)};
        DrawTextLine(dc, L"Syncing…", syncRect, inkSecondary, captionFont, DT_RIGHT | DT_VCENTER | DT_SINGLELINE);
    }

    AppConfig config;
    std::vector<UsageRow> rows = SnapshotRows(&config);
    std::vector<FlyoutGroupInfo> groups = FlyoutGroupCounts(rows);
    int shownRows = 0;
    int y = S(52);

    auto drawUsageRow = [&](const UsageRow& row, RECT line, COLORREF from, COLORREF to) {
        int left = line.left + S(16);
        int right = line.right - S(16);
        std::wstring name = row.label.empty() ? (row.provider.empty() ? L"Codex" : row.provider) : row.label;
        bool hasData = row.primaryPercent >= 0 || row.secondaryPercent >= 0;
        if (hasData) {
            int primaryLeft = UsageDisplayPercent(row.primaryPercent);
            int secondaryLeft = UsageDisplayPercent(row.secondaryPercent);
            std::wstring sub = !row.plan.empty()
                ? row.plan
                : (primaryLeft >= 0 && secondaryLeft >= 0 ? L"wk " + PercentText(secondaryLeft) : L"");
            int subWidth = sub.empty() ? 0 : S(74);
            RECT nameRect{left, line.top + S(8), right - S(64) - subWidth, line.top + S(34)};
            DrawTextLine(dc, name, nameRect, ink, bodyFont, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS);
            int leading = primaryLeft >= 0 ? primaryLeft : secondaryLeft;
            RECT pctRect{right - S(60) - subWidth, line.top + S(8), right - subWidth, line.top + S(34)};
            DrawTextLine(dc, PercentText(leading), pctRect, ink, percentFont, DT_RIGHT | DT_VCENTER | DT_SINGLELINE);
            if (!sub.empty()) {
                RECT subRect{right - subWidth + S(8), line.top + S(11), right, line.top + S(34)};
                DrawTextLine(dc, sub, subRect, inkSecondary, captionFont, DT_RIGHT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS);
            }
            RECT bar{left, line.top + S(42), right, line.top + S(49)};
            DrawGradientBar(dc, bar, leading, track, from, to);
        } else {
            RECT nameRect{left, line.top + S(8), right - S(140), line.top + S(34)};
            DrawTextLine(dc, name, nameRect, ink, bodyFont, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS);
            std::wstring status = row.status.empty() ? L"No data" : row.status;
            bool alert = row.status == L"Needs login" || row.status == L"Error" ||
                         row.status == L"CLI missing" || row.status == L"Timeout";
            RECT statusRect{right - S(150), line.top + S(9), right, line.top + S(34)};
            DrawTextLine(dc, status, statusRect, alert ? palette.danger : inkSecondary, captionFont,
                         DT_RIGHT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS);
            RECT bar{left, line.top + S(42), right, line.top + S(49)};
            DrawGradientBar(dc, bar, -1, track, from, to);
        }
    };

    if (groups.empty()) {
        y += S(8);
        RECT card{padX, y, client.right - padX, y + S(76)};
        DrawRoundRectColor(dc, card, S(8), cardFill, cardBorder);
        RECT emptyTitle{card.left + S(16), card.top + S(12), card.right - S(16), card.top + S(38)};
        DrawTextLine(dc, L"No usage data yet", emptyTitle, ink, bodyFont, DT_LEFT | DT_VCENTER | DT_SINGLELINE);
        RECT emptyBody{card.left + S(16), card.top + S(38), card.right - S(16), card.bottom - S(10)};
        DrawTextLine(dc, L"Refresh to load Codex and Claude usage.", emptyBody, inkSecondary, captionFont,
                     DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS);
        y = card.bottom;
    } else {
        for (const auto& group : groups) {
            y += S(8);
            RECT card{padX, y, client.right - padX, y + S(56) + group.rowCount * S(64)};
            DrawRoundRectColor(dc, card, S(8), cardFill, cardBorder);
            bool isClaude = _wcsicmp(group.provider.c_str(), L"Claude") == 0;
            COLORREF from = isClaude ? claudeFrom : codexFrom;
            COLORREF to = isClaude ? claudeTo : codexTo;
            COLORREF badgeFillColor = isClaude ? palette.warningSoft : palette.accentSoft;
            COLORREF badgeBorderColor = BlendColor(cardBorder, isClaude ? palette.warning : palette.accent, 28);
            COLORREF badgeInkColor = isClaude ? palette.warning : palette.accent;

            RECT glyphBox{card.left + S(16), card.top + S(13), card.left + S(44), card.top + S(41)};
            DrawRoundRectColor(dc, glyphBox, S(8), badgeFillColor, badgeBorderColor);
            DrawTextLine(dc, group.provider.substr(0, 1), glyphBox, badgeInkColor, badgeFont,
                         DT_CENTER | DT_VCENTER | DT_SINGLELINE);
            RECT providerName{glyphBox.right + S(12), card.top + S(9), card.right - S(46), card.top + S(45)};
            DrawTextLine(dc, group.provider, providerName, ink, sectionFont, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS);
            RECT chevron{card.right - S(38), card.top + S(11), card.right - S(14), card.top + S(43)};
            DrawTextLine(dc, kGlyphChevronRight, chevron, inkSecondary, iconFont, DT_CENTER | DT_VCENTER | DT_SINGLELINE);

            int rowY = card.top + S(52);
            int drawn = 0;
            for (const auto& row : rows) {
                std::wstring rowProvider = row.provider.empty() ? L"Codex" : row.provider;
                if (_wcsicmp(rowProvider.c_str(), group.provider.c_str()) != 0) continue;
                if (drawn >= group.rowCount) break;
                RECT line{card.left, rowY, card.right, rowY + S(64)};
                drawUsageRow(row, line, from, to);
                rowY += S(64);
                ++drawn;
                ++shownRows;
            }
            y = card.bottom;
        }
    }

    y += S(10);
    std::wstring lastRefreshText = L"Last refresh: --";
    {
        std::lock_guard<std::mutex> lock(g_app.rowsMutex);
        if (g_app.hasLastRefresh) {
            wchar_t clock[16]{};
            swprintf(clock, 16, L"%02u:%02u", g_app.lastRefreshLocal.wHour, g_app.lastRefreshLocal.wMinute);
            lastRefreshText = std::wstring(L"Last refresh: ") + clock;
        }
    }
    RECT metaOne{padX + S(2), y, client.right - padX, y + S(22)};
    DrawTextLine(dc, lastRefreshText, metaOne, inkSecondary, captionFont, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS);
    int hiddenRows = static_cast<int>(rows.size()) - shownRows;
    std::wstring autoText = L"Auto-refresh every " + std::to_wstring(config.refreshIntervalSeconds) + L" s";
    if (hiddenRows > 0) autoText += L" · +" + std::to_wstring(hiddenRows) + L" more in Settings";
    RECT metaTwo{padX + S(2), y + S(22), client.right - padX, y + S(44)};
    DrawTextLine(dc, autoText, metaTwo, inkSecondary, captionFont, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS);

    int footerTop = client.bottom - S(64);
    RECT footerSeparator{client.left, footerTop, client.right, footerTop + 1};
    FillRectColor(dc, footerSeparator, hairline);

    FlyoutActionRects actions = BuildFlyoutActionRects(hwnd);
    auto drawCommandButton = [&](RECT rect, const wchar_t* glyph, const std::wstring& label, UiAction action, bool disabled) {
        bool hovered = !disabled && SameTarget(g_app.flyoutHoverTarget, action, -1);
        bool pressed = !disabled && SameTarget(g_app.flyoutPressedTarget, action, -1);
        COLORREF fill = pressed
            ? palette.controlPressed
            : hovered
                ? palette.controlHover
                : palette.control;
        DrawRoundRectColor(dc, rect, S(6), fill, cardBorder);
        COLORREF buttonInk = disabled ? palette.subtle : ink;
        if (label.empty()) {
            DrawTextLine(dc, glyph, rect, buttonInk, iconFont, DT_CENTER | DT_VCENTER | DT_SINGLELINE);
        } else {
            RECT glyphRect{rect.left + S(12), rect.top, rect.left + S(28), rect.bottom};
            DrawTextLine(dc, glyph, glyphRect, buttonInk, iconFont, DT_LEFT | DT_VCENTER | DT_SINGLELINE);
            RECT labelRect{rect.left + S(32), rect.top, rect.right - S(8), rect.bottom};
            DrawTextLine(dc, label, labelRect, buttonInk, captionFont, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS);
        }
    };
    drawCommandButton(actions.refresh, kGlyphRefresh, g_app.refreshing ? L"Refreshing" : L"Refresh", UiAction::Refresh, g_app.refreshing);
    drawCommandButton(actions.openConfig, kGlyphDocument, L"Config", UiAction::OpenConfig, false);
    drawCommandButton(actions.settings, kGlyphSettings, L"", UiAction::EditConfig, false);

    DeleteObject(titleFont);
    DeleteObject(sectionFont);
    DeleteObject(bodyFont);
    DeleteObject(percentFont);
    DeleteObject(captionFont);
    DeleteObject(badgeFont);
    DeleteObject(iconFont);

    if (dc != windowDc) {
        BitBlt(windowDc, 0, 0, width, height, dc, 0, 0, SRCCOPY);
    }
    if (oldBitmap) SelectObject(bufferDc, oldBitmap);
    if (bitmap) DeleteObject(bitmap);
    if (bufferDc) DeleteDC(bufferDc);
    EndPaint(hwnd, &ps);
}

bool TaskbarWidgetHitTest(HWND hwnd, POINT point) {
    TaskbarPresenceState* state = GetTaskbarPresenceState(hwnd);
    if (!state) return true;
    return PtInRect(&state->widgetClientRect, point) != FALSE;
}

LRESULT CALLBACK TaskbarPresenceProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    switch (msg) {
        case WM_MOUSEACTIVATE:
            return MA_NOACTIVATE;

        case WM_GETOBJECT:
        case WM_NCCALCSIZE:
        case WM_IME_SETCONTEXT:
        case WM_IME_NOTIFY:
            return 0;

        case WM_NCHITTEST: {
            POINT point{GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam)};
            ScreenToClient(hwnd, &point);
            return TaskbarWidgetHitTest(hwnd, point) ? HTCLIENT : HTTRANSPARENT;
        }

        case WM_PAINT:
            PaintTaskbarPresence(hwnd);
            return 0;

        case WM_ERASEBKGND:
            return 1;

        case WM_MOUSEMOVE: {
            POINT point{GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam)};
            if (!TaskbarWidgetHitTest(hwnd, point)) {
                if (g_app.taskbarPresenceHover) {
                    g_app.taskbarPresenceHover = false;
                    g_app.taskbarPresencePressed = false;
                    InvalidateRect(hwnd, nullptr, FALSE);
                }
                return 0;
            }
            if (!g_app.taskbarPresenceHover) {
                g_app.taskbarPresenceHover = true;
                InvalidateRect(hwnd, nullptr, FALSE);
            }
            SetCursor(LoadCursorW(nullptr, IDC_HAND));
            if (!g_app.taskbarPresenceTrackingMouseLeave) {
                TRACKMOUSEEVENT event{};
                event.cbSize = sizeof(event);
                event.dwFlags = TME_LEAVE;
                event.hwndTrack = hwnd;
                if (TrackMouseEvent(&event)) g_app.taskbarPresenceTrackingMouseLeave = true;
            }
            return 0;
        }

        case WM_MOUSELEAVE:
            g_app.taskbarPresenceHover = false;
            g_app.taskbarPresencePressed = false;
            g_app.taskbarPresenceTrackingMouseLeave = false;
            InvalidateRect(hwnd, nullptr, FALSE);
            return 0;

        case WM_LBUTTONDOWN: {
            POINT point{GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam)};
            if (!TaskbarWidgetHitTest(hwnd, point)) return 0;
            g_app.taskbarPresencePressed = true;
            SetCapture(hwnd);
            InvalidateRect(hwnd, nullptr, FALSE);
            return 0;
        }

        case WM_LBUTTONUP: {
            POINT point{GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam)};
            bool inside = TaskbarWidgetHitTest(hwnd, point);
            if (GetCapture() == hwnd) ReleaseCapture();
            if (g_app.taskbarPresencePressed && inside) {
                g_app.taskbarPresencePressed = false;
                if (!ActivateTaskbarPresenceWindow(hwnd)) {
                    GetWindowRect(hwnd, &g_app.taskbarPresenceScreenRect);
                }
                InvalidateRect(hwnd, nullptr, FALSE);
                ToggleCodexBarFlyout(hwnd);
            } else if (g_app.taskbarPresencePressed) {
                g_app.taskbarPresencePressed = false;
                InvalidateRect(hwnd, nullptr, FALSE);
            }
            return 0;
        }

        case WM_RBUTTONUP:
            ShowContextMenu(g_app.mainWindow ? g_app.mainWindow : hwnd);
            return 0;

        case WM_DPICHANGED:
            PositionTaskbarPresence();
            return 0;

        case WM_NCDESTROY: {
            TaskbarPresenceState* state = GetTaskbarPresenceState(hwnd);
            if (state) {
                SetWindowLongPtrW(hwnd, GWLP_USERDATA, 0);
                delete state;
            }
            return 0;
        }

        default:
            return DefWindowProcW(hwnd, msg, wParam, lParam);
    }
}

LRESULT CALLBACK CodexBarFlyoutProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    auto targetAtPoint = [&](POINT point) -> TargetKey {
        FlyoutActionRects actions = BuildFlyoutActionRects(hwnd);
        if (PtInRect(&actions.refresh, point)) return TargetKey{true, UiAction::Refresh, -1};
        if (PtInRect(&actions.settings, point)) return TargetKey{true, UiAction::EditConfig, -1};
        if (PtInRect(&actions.openConfig, point)) return TargetKey{true, UiAction::OpenConfig, -1};
        return TargetKey{};
    };

    switch (msg) {
        case WM_PAINT:
            PaintCodexBarFlyout(hwnd);
            return 0;

        case WM_ERASEBKGND:
            return 1;

        case WM_KEYDOWN:
            if (wParam == VK_ESCAPE) {
                HideCodexBarFlyout();
                return 0;
            }
            break;

        case WM_MOUSEMOVE: {
            TouchCodexBarFlyout();
            POINT point{GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam)};
            TargetKey next = targetAtPoint(point);
            if (next.valid) SetCursor(LoadCursorW(nullptr, IDC_HAND));
            bool changed = g_app.flyoutHoverTarget.valid != next.valid ||
                           g_app.flyoutHoverTarget.action != next.action ||
                           g_app.flyoutHoverTarget.profileIndex != next.profileIndex;
            if (changed) {
                g_app.flyoutHoverTarget = next;
                InvalidateRect(hwnd, nullptr, FALSE);
            }
            if (next.valid && !g_app.flyoutTrackingMouseLeave) {
                TRACKMOUSEEVENT event{};
                event.cbSize = sizeof(event);
                event.dwFlags = TME_LEAVE;
                event.hwndTrack = hwnd;
                if (TrackMouseEvent(&event)) g_app.flyoutTrackingMouseLeave = true;
            }
            return 0;
        }

        case WM_MOUSELEAVE:
            g_app.flyoutHoverTarget = TargetKey{};
            g_app.flyoutPressedTarget = TargetKey{};
            g_app.flyoutTrackingMouseLeave = false;
            InvalidateRect(hwnd, nullptr, FALSE);
            return 0;

        case WM_LBUTTONDOWN: {
            TouchCodexBarFlyout();
            POINT point{GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam)};
            TargetKey target = targetAtPoint(point);
            if (target.valid) {
                g_app.flyoutPressedTarget = target;
                SetCapture(hwnd);
                InvalidateRect(hwnd, nullptr, FALSE);
            }
            return 0;
        }

        case WM_LBUTTONUP: {
            TouchCodexBarFlyout();
            POINT point{GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam)};
            TargetKey released = targetAtPoint(point);
            TargetKey pressed = g_app.flyoutPressedTarget;
            g_app.flyoutPressedTarget = TargetKey{};
            if (GetCapture() == hwnd) ReleaseCapture();
            g_app.flyoutHoverTarget = released;
            InvalidateRect(hwnd, nullptr, FALSE);
            if (!pressed.valid || !released.valid ||
                pressed.action != released.action || pressed.profileIndex != released.profileIndex) {
                return 0;
            }
            if (released.action == UiAction::Refresh) {
                RefreshAsync();
                InvalidateRect(hwnd, nullptr, FALSE);
                return 0;
            }
            if (released.action == UiAction::EditConfig) {
                HideCodexBarFlyout();
                ShowSettingsWindow();
                return 0;
            }
            if (released.action == UiAction::OpenConfig) {
                OpenConfigFile(hwnd);
                return 0;
            }
            return 0;
        }

        default:
            return DefWindowProcW(hwnd, msg, wParam, lParam);
    }
    return DefWindowProcW(hwnd, msg, wParam, lParam);
}

void EnsureSettingsFonts(UINT dpi) {
    if (g_app.settingsFont && g_app.settingsTitleFont && g_app.settingsFontDpi == dpi) {
        return;
    }
    if (g_app.settingsFont) DeleteObject(g_app.settingsFont);
    if (g_app.settingsTitleFont) DeleteObject(g_app.settingsTitleFont);
    g_app.settingsFontDpi = dpi;
    g_app.settingsFont = CreateFontW(
        ScaleForDpi(17, dpi),
        0,
        0,
        0,
        FW_NORMAL,
        FALSE,
        FALSE,
        FALSE,
        DEFAULT_CHARSET,
        OUT_DEFAULT_PRECIS,
        CLIP_DEFAULT_PRECIS,
        CLEARTYPE_QUALITY,
        DEFAULT_PITCH,
        L"Segoe UI"
    );
    g_app.settingsTitleFont = CreateFontW(
        ScaleForDpi(22, dpi),
        0,
        0,
        0,
        FW_SEMIBOLD,
        FALSE,
        FALSE,
        FALSE,
        DEFAULT_CHARSET,
        OUT_DEFAULT_PRECIS,
        CLIP_DEFAULT_PRECIS,
        CLEARTYPE_QUALITY,
        DEFAULT_PITCH,
        L"Segoe UI Variable"
    );
}

void SetControlFont(HWND hwnd) {
    if (g_app.settingsFont) {
        SendMessageW(hwnd, WM_SETFONT, reinterpret_cast<WPARAM>(g_app.settingsFont), TRUE);
    }
}

HWND CreateSettingsButton(HWND parent, const std::wstring& text, int id, int x, int y, int width, int height) {
    HWND hwnd = CreateWindowW(
        L"BUTTON",
        text.c_str(),
        WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_OWNERDRAW,
        x,
        y,
        width,
        height,
        parent,
        reinterpret_cast<HMENU>(static_cast<INT_PTR>(id)),
        g_app.instance,
        nullptr
    );
    SetControlFont(hwnd);
    return hwnd;
}

struct SettingsLayout {
    int left = 0;
    int right = 0;
    RECT cardRefresh{};
    RECT cardClaude{};
    RECT cardAcrylic{};
    int profilesHeaderTop = 0;
    int profileRowTop = 0;
    int profileRowHeight = 0;
    int profileCardHeight = 0;
    int footerTop = 0;
};

SettingsLayout BuildSettingsLayout(HWND hwnd) {
    RECT client{};
    GetClientRect(hwnd, &client);
    UiScale S{GetDpiForHwnd(hwnd)};
    SettingsLayout layout;
    int margin = S(24);
    int contentWidth = std::max(S(360), std::min(static_cast<int>(client.right) - margin * 2, S(640)));
    layout.left = margin;
    layout.right = layout.left + contentWidth;
    int y = S(70);
    int cardHeight = S(56);
    y += S(26);
    layout.cardRefresh = RECT{layout.left, y, layout.right, y + cardHeight};
    y += cardHeight + S(6);
    layout.cardClaude = RECT{layout.left, y, layout.right, y + cardHeight};
    y += cardHeight + S(6);
    layout.cardAcrylic = RECT{layout.left, y, layout.right, y + cardHeight};
    y += cardHeight + S(20);
    layout.profilesHeaderTop = y;
    layout.profileRowTop = y + S(34);
    layout.profileRowHeight = S(60);
    layout.profileCardHeight = S(54);
    layout.footerTop = std::max(layout.profileRowTop + S(70), static_cast<int>(client.bottom) - S(58));
    return layout;
}

RECT SettingsRefreshEditFrame(const SettingsLayout& layout, const UiScale& S) {
    int width = S(78);
    int height = S(28);
    return RECT{
        layout.cardRefresh.right - S(20) - width,
        layout.cardRefresh.top + (RectHeight(layout.cardRefresh) - height) / 2,
        layout.cardRefresh.right - S(20),
        layout.cardRefresh.top + (RectHeight(layout.cardRefresh) + height) / 2
    };
}

RECT SettingsRefreshEditChildRect(const SettingsLayout& layout, const UiScale& S) {
    RECT rect = SettingsRefreshEditFrame(layout, S);
    InflateRect(&rect, -S(9), -S(5));
    return rect;
}

RECT SettingsToggleRect(const RECT& card, const UiScale& S) {
    int width = S(44);
    int height = S(24);
    return RECT{
        card.right - S(20) - width,
        card.top + (RectHeight(card) - height) / 2,
        card.right - S(20),
        card.top + (RectHeight(card) + height) / 2
    };
}

void DestroySettingsChildren(HWND hwnd) {
    std::vector<HWND> children;
    EnumChildWindows(hwnd, [](HWND child, LPARAM lParam) -> BOOL {
        auto* children = reinterpret_cast<std::vector<HWND>*>(lParam);
        children->push_back(child);
        return TRUE;
    }, reinterpret_cast<LPARAM>(&children));
    for (HWND child : children) {
        DestroyWindow(child);
    }
}

void ApplyDarkControlTheme(HWND hwnd) {
    if (!hwnd) return;
    HMODULE uxtheme = LoadLibraryW(L"uxtheme.dll");
    if (!uxtheme) return;
    using SetWindowThemeFn = HRESULT (WINAPI*)(HWND, LPCWSTR, LPCWSTR);
#if defined(__GNUC__)
#pragma GCC diagnostic push
#pragma GCC diagnostic ignored "-Wcast-function-type"
#endif
    auto setTheme = reinterpret_cast<SetWindowThemeFn>(GetProcAddress(uxtheme, "SetWindowTheme"));
#if defined(__GNUC__)
#pragma GCC diagnostic pop
#endif
    if (setTheme) setTheme(hwnd, IsWindowsDarkMode() ? L"DarkMode_Explorer" : L"Explorer", nullptr);
    FreeLibrary(uxtheme);
}

void PopulateSettingsWindow(HWND hwnd, bool preserveUserValues) {
    if (!hwnd || !IsWindow(hwnd)) return;
    std::wstring preservedRefreshText;
    bool preservedClaude = false;
    bool hasPreservedClaude = false;
    bool preservedAcrylic = false;
    bool hasPreservedAcrylic = false;
    if (preserveUserValues) {
        HWND existingRefresh = GetDlgItem(hwnd, CONTROL_SETTINGS_REFRESH_EDIT);
        if (existingRefresh) preservedRefreshText = WindowText(existingRefresh);
        HWND existingClaude = GetDlgItem(hwnd, CONTROL_SETTINGS_CLAUDE_CHECK);
        if (existingClaude) {
            preservedClaude = ControlChecked(existingClaude);
            hasPreservedClaude = true;
        }
        HWND existingAcrylic = GetDlgItem(hwnd, CONTROL_SETTINGS_ACRYLIC_CHECK);
        if (existingAcrylic) {
            preservedAcrylic = ControlChecked(existingAcrylic);
            hasPreservedAcrylic = true;
        }
    }
    SendMessageW(hwnd, WM_SETREDRAW, FALSE, 0);
    DestroySettingsChildren(hwnd);

    UINT dpi = GetDpiForHwnd(hwnd);
    EnsureSettingsFonts(dpi);
    UiScale S{dpi};
    AppConfig config = CurrentConfigSnapshot();
    SettingsLayout layout = BuildSettingsLayout(hwnd);

    RECT editRect = SettingsRefreshEditChildRect(layout, S);
    HWND edit = CreateWindowExW(
        0,
        L"EDIT",
        (preservedRefreshText.empty() ? std::to_wstring(config.refreshIntervalSeconds) : preservedRefreshText).c_str(),
        WS_CHILD | WS_VISIBLE | WS_TABSTOP | ES_NUMBER | ES_AUTOHSCROLL | ES_CENTER,
        editRect.left,
        editRect.top,
        editRect.right - editRect.left,
        editRect.bottom - editRect.top,
        hwnd,
        reinterpret_cast<HMENU>(static_cast<INT_PTR>(CONTROL_SETTINGS_REFRESH_EDIT)),
        g_app.instance,
        nullptr
    );
    SetControlFont(edit);
    ApplyDarkControlTheme(edit);

    RECT claudeRect = SettingsToggleRect(layout.cardClaude, S);
    HWND claude = CreateSettingsButton(
        hwnd,
        L"",
        CONTROL_SETTINGS_CLAUDE_CHECK,
        claudeRect.left,
        claudeRect.top,
        claudeRect.right - claudeRect.left,
        claudeRect.bottom - claudeRect.top
    );
    SetControlChecked(claude, hasPreservedClaude ? preservedClaude : config.claude.enabled);

    RECT acrylicRect = SettingsToggleRect(layout.cardAcrylic, S);
    HWND acrylic = CreateSettingsButton(
        hwnd,
        L"",
        CONTROL_SETTINGS_ACRYLIC_CHECK,
        acrylicRect.left,
        acrylicRect.top,
        acrylicRect.right - acrylicRect.left,
        acrylicRect.bottom - acrylicRect.top
    );
    bool acrylicChecked = hasPreservedAcrylic ? preservedAcrylic : (config.flyoutStyle != L"solid");
    SetControlChecked(acrylic, acrylicChecked);

    CreateSettingsButton(hwnd, L"New profile", CONTROL_SETTINGS_ADD_PROFILE, layout.right - S(108), layout.profilesHeaderTop, S(108), S(30));

    int rowY = layout.profileRowTop;
    int rowHeight = layout.profileRowHeight;
    int cardHeight = layout.profileCardHeight;
    size_t visibleCount = std::min<size_t>(config.codexProfiles.size(), CONTROL_SETTINGS_PROFILE_LIMIT + 1);
    for (size_t i = 0; i < visibleCount; ++i) {
        const auto& profile = config.codexProfiles[i];
        int index = static_cast<int>(i);
        int buttonY = rowY + (cardHeight - S(28)) / 2;
        int x = layout.right - S(12);
        x -= S(68);
        CreateSettingsButton(hwnd, L"Folder", CONTROL_SETTINGS_FOLDER_BASE + index, x, buttonY, S(68), S(28));
        x -= S(78) + S(6);
        CreateSettingsButton(hwnd, profile.enabled ? L"Disable" : L"Enable", CONTROL_SETTINGS_TOGGLE_BASE + index, x, buttonY, S(78), S(28));
        x -= S(74) + S(6);
        CreateSettingsButton(hwnd, L"Rename", CONTROL_SETTINGS_RENAME_BASE + index, x, buttonY, S(74), S(28));
        x -= S(64) + S(6);
        CreateSettingsButton(hwnd, L"Login", CONTROL_SETTINGS_LOGIN_BASE + index, x, buttonY, S(64), S(28));
        rowY += rowHeight;
    }

    int footerButtonY = layout.footerTop + S(12);
    CreateSettingsButton(hwnd, L"Open config", CONTROL_SETTINGS_OPEN_CONFIG, layout.left, footerButtonY, S(104), S(32));
    CreateSettingsButton(hwnd, L"Profiles folder", CONTROL_SETTINGS_OPEN_PROFILES, layout.left + S(112), footerButtonY, S(118), S(32));
    CreateSettingsButton(hwnd, L"Save", CONTROL_SETTINGS_SAVE, layout.right - S(82), footerButtonY, S(82), S(32));
    CreateSettingsButton(hwnd, L"Refresh now", CONTROL_SETTINGS_REFRESH_NOW, layout.right - S(82) - S(8) - S(104), footerButtonY, S(104), S(32));
    SendMessageW(hwnd, DM_SETDEFID, CONTROL_SETTINGS_SAVE, 0);
    SendMessageW(hwnd, WM_SETREDRAW, TRUE, 0);
    RedrawWindow(hwnd, nullptr, nullptr, RDW_INVALIDATE | RDW_UPDATENOW | RDW_ALLCHILDREN);
}

void PaintSettingsWindow(HWND hwnd) {
    PAINTSTRUCT ps{};
    HDC windowDc = BeginPaint(hwnd, &ps);
    RECT client{};
    GetClientRect(hwnd, &client);
    int width = std::max(1, static_cast<int>(client.right - client.left));
    int height = std::max(1, static_cast<int>(client.bottom - client.top));

    HDC bufferDc = CreateCompatibleDC(windowDc);
    HBITMAP bitmap = bufferDc ? CreateCompatibleBitmap(windowDc, width, height) : nullptr;
    HGDIOBJ oldBitmap = nullptr;
    HDC dc = windowDc;
    if (bufferDc && bitmap) {
        oldBitmap = SelectObject(bufferDc, bitmap);
        dc = bufferDc;
    }

    UINT dpi = GetDpiForHwnd(hwnd);
    EnsureSettingsFonts(dpi);
    UiScale S{dpi};
    FluentPalette palette = CurrentPalette();
    SettingsLayout layout = BuildSettingsLayout(hwnd);
    AppConfig config = CurrentConfigSnapshot();

    COLORREF bg = palette.page;
    COLORREF ink = palette.text;
    COLORREF inkSecondary = palette.muted;
    COLORREF cardFill = palette.surface;
    COLORREF cardBorder = palette.border;
    COLORREF hairline = palette.border;

    FillRectColor(dc, client, bg);

    HFONT titleFont = CreateFontW(S(32), 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                                  OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                                  DEFAULT_PITCH, L"Segoe UI Variable Display");
    HFONT sectionFont = CreateFontW(S(18), 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                                    OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                                    DEFAULT_PITCH, L"Segoe UI Variable");
    HFONT bodyFont = CreateFontW(S(17), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                                 OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                                 DEFAULT_PITCH, L"Segoe UI Variable");
    HFONT captionFont = CreateFontW(S(14), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                                    OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                                    DEFAULT_PITCH, L"Segoe UI");
    HFONT iconFont = CreateFontW(S(20), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                                 OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                                 DEFAULT_PITCH, L"Segoe Fluent Icons");

    RECT title{layout.left, S(20), layout.right, S(56)};
    DrawTextLine(dc, L"Settings", title, ink, titleFont, DT_LEFT | DT_VCENTER | DT_SINGLELINE);

    RECT generalHeader{layout.left + S(2), S(70), layout.right, S(94)};
    DrawTextLine(dc, L"General", generalHeader, ink, sectionFont, DT_LEFT | DT_VCENTER | DT_SINGLELINE);

    auto drawCard = [&](RECT rect, const wchar_t* glyph, const std::wstring& cardTitle, const std::wstring& cardDesc) {
        DrawRoundRectColor(dc, rect, S(8), cardFill, cardBorder);
        RECT glyphRect{rect.left + S(16), rect.top, rect.left + S(42), rect.bottom};
        DrawTextLine(dc, glyph, glyphRect, inkSecondary, iconFont, DT_CENTER | DT_VCENTER | DT_SINGLELINE);
        RECT titleRect{rect.left + S(52), rect.top + S(8), rect.right - S(120), rect.top + S(30)};
        DrawTextLine(dc, cardTitle, titleRect, ink, bodyFont, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS);
        RECT descRect{rect.left + S(52), rect.top + S(30), rect.right - S(120), rect.bottom - S(8)};
        DrawTextLine(dc, cardDesc, descRect, inkSecondary, captionFont, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS);
    };
    drawCard(layout.cardRefresh, kGlyphClock, L"Refresh interval", L"Seconds between provider usage refreshes");
    drawCard(layout.cardClaude, kGlyphChat, L"Claude provider", L"Include Claude CLI usage in the flyout");
    drawCard(layout.cardAcrylic, kGlyphColor, L"Acrylic flyout", L"Translucent blur behind the taskbar flyout");

    RECT editFrame = SettingsRefreshEditFrame(layout, S);
    bool editFocused = GetFocus() == GetDlgItem(hwnd, CONTROL_SETTINGS_REFRESH_EDIT);
    DrawRoundRectColor(dc, editFrame, S(8), palette.control,
                       editFocused ? palette.accent : palette.borderStrong);

    RECT profilesHeader{layout.left + S(2), layout.profilesHeaderTop, layout.right - S(120), layout.profilesHeaderTop + S(30)};
    DrawTextLine(dc, L"Codex profiles", profilesHeader, ink, sectionFont, DT_LEFT | DT_VCENTER | DT_SINGLELINE);

    int rowY = layout.profileRowTop;
    int rowHeight = layout.profileRowHeight;
    int cardHeight = layout.profileCardHeight;
    size_t visibleCount = std::min<size_t>(config.codexProfiles.size(), CONTROL_SETTINGS_PROFILE_LIMIT + 1);
    for (size_t i = 0; i < visibleCount; ++i) {
        const auto& profile = config.codexProfiles[i];
        RECT row{layout.left, rowY, layout.right, rowY + cardHeight};
        DrawRoundRectColor(dc, row, S(8), cardFill, cardBorder);
        COLORREF dot = profile.enabled ? palette.success : inkSecondary;
        DrawStatusDot(dc, row.left + S(20), rowY + cardHeight / 2, S(4), dot);
        std::wstring label = profile.label + (profile.enabled ? L"" : L"  (disabled)");
        RECT nameRect{row.left + S(36), row.top + S(7), row.left + S(300), row.top + S(29)};
        DrawTextLine(dc, label, nameRect, ink, bodyFont, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS);
        RECT pathRect{row.left + S(36), row.top + S(29), row.left + S(320), row.bottom - S(7)};
        DrawTextLine(dc, ProfilePathLabel(profile.codexHome), pathRect, inkSecondary, captionFont, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS);
        rowY += rowHeight;
    }
    if (config.codexProfiles.empty()) {
        RECT row{layout.left, rowY, layout.right, rowY + cardHeight};
        DrawRoundRectColor(dc, row, S(8), cardFill, cardBorder);
        RECT emptyRect{row.left + S(18), row.top, row.right - S(18), row.bottom};
        DrawTextLine(dc, L"No Codex profiles configured.", emptyRect, inkSecondary, captionFont, DT_LEFT | DT_VCENTER | DT_SINGLELINE);
    }

    RECT footerLine{layout.left, layout.footerTop, layout.right, layout.footerTop + 1};
    FillRectColor(dc, footerLine, hairline);

    DeleteObject(titleFont);
    DeleteObject(sectionFont);
    DeleteObject(bodyFont);
    DeleteObject(captionFont);
    DeleteObject(iconFont);

    if (dc != windowDc) {
        BitBlt(windowDc, 0, 0, width, height, dc, 0, 0, SRCCOPY);
    }
    if (oldBitmap) SelectObject(bufferDc, oldBitmap);
    if (bitmap) DeleteObject(bitmap);
    if (bufferDc) DeleteDC(bufferDc);
    EndPaint(hwnd, &ps);
}

bool DrawSettingsButtonControl(const DRAWITEMSTRUCT* item) {
    if (!item || item->CtlType != ODT_BUTTON || !item->hwndItem) return false;

    UINT dpi = GetDpiForHwnd(item->hwndItem);
    UiScale S{dpi};
    FluentPalette palette = CurrentPalette();
    auto settingsControlBackground = [&](int id) {
        bool insideCard = id == CONTROL_SETTINGS_CLAUDE_CHECK ||
                          id == CONTROL_SETTINGS_ACRYLIC_CHECK ||
                          (id >= CONTROL_SETTINGS_LOGIN_BASE && id <= CONTROL_SETTINGS_LOGIN_BASE + CONTROL_SETTINGS_PROFILE_LIMIT) ||
                          (id >= CONTROL_SETTINGS_RENAME_BASE && id <= CONTROL_SETTINGS_RENAME_BASE + CONTROL_SETTINGS_PROFILE_LIMIT) ||
                          (id >= CONTROL_SETTINGS_TOGGLE_BASE && id <= CONTROL_SETTINGS_TOGGLE_BASE + CONTROL_SETTINGS_PROFILE_LIMIT) ||
                          (id >= CONTROL_SETTINGS_FOLDER_BASE && id <= CONTROL_SETTINGS_FOLDER_BASE + CONTROL_SETTINGS_PROFILE_LIMIT);
        return insideCard ? palette.surface : palette.page;
    };
    FillRectColor(item->hDC, item->rcItem, settingsControlBackground(item->CtlID));

    if (item->CtlID == CONTROL_SETTINGS_CLAUDE_CHECK || item->CtlID == CONTROL_SETTINGS_ACRYLIC_CHECK) {
        RECT rect = item->rcItem;
        InflateRect(&rect, -S(1), -S(1));
        bool disabled = (item->itemState & ODS_DISABLED) != 0;
        bool pressed = (item->itemState & ODS_SELECTED) != 0;
        bool focused = (item->itemState & ODS_FOCUS) != 0;
        DrawToggleSwitch(item->hDC, rect, ControlChecked(item->hwndItem), focused, pressed, disabled);
        return true;
    }

    std::wstring text = WindowText(item->hwndItem);

    bool disabled = (item->itemState & ODS_DISABLED) != 0;
    bool pressed = (item->itemState & ODS_SELECTED) != 0;
    bool focused = (item->itemState & ODS_FOCUS) != 0;
    bool hovered = (item->itemState & ODS_HOTLIGHT) != 0;
    bool primary = item->CtlID == CONTROL_SETTINGS_SAVE;

    HFONT font = g_app.settingsFont;
    if (!font) {
        EnsureSettingsFonts(dpi);
        font = g_app.settingsFont;
    }

    RECT rect = item->rcItem;
    InflateRect(&rect, -S(1), -S(1));
    DrawButton(item->hDC, rect, text, font, primary, disabled, hovered, pressed);
    if (focused) {
        RECT focus = rect;
        InflateRect(&focus, -S(2), -S(2));
        DrawRoundRectOutline(item->hDC, focus, S(5), palette.accent);
    }
    return true;
}

bool SaveSettingsFromWindow(HWND hwnd) {
    AppConfig config = CurrentConfigSnapshot();
    HWND edit = GetDlgItem(hwnd, CONTROL_SETTINGS_REFRESH_EDIT);
    std::wstring refreshText = edit ? WindowText(edit) : L"";
    auto parsed = ParseConfigInt(refreshText);
    if (!parsed) {
        MessageBoxW(hwnd, L"Refresh interval must be a number.", kAppTitle, MB_OK | MB_ICONWARNING);
        return false;
    }
    config.refreshIntervalSeconds = std::min(86400, std::max(30, *parsed));

    HWND claude = GetDlgItem(hwnd, CONTROL_SETTINGS_CLAUDE_CHECK);
    config.claude.enabled = claude && ControlChecked(claude);

    HWND acrylic = GetDlgItem(hwnd, CONTROL_SETTINGS_ACRYLIC_CHECK);
    if (acrylic) {
        config.flyoutStyle = ControlChecked(acrylic) ? L"acrylic" : L"solid";
    }

    bool saved = SaveAndApplyConfig(g_app.mainWindow ? g_app.mainWindow : hwnd, config, true);
    if (saved) {
        ConfigureFlyoutMaterial(g_app.codexBarFlyoutWindow);
        PopulateSettingsWindow(hwnd);
        InvalidateRect(hwnd, nullptr, FALSE);
    }
    return saved;
}

HWND EnsureSettingsWindow() {
    if (g_app.settingsWindow && IsWindow(g_app.settingsWindow)) {
        return g_app.settingsWindow;
    }

    UINT dpi = g_app.mainWindow && IsWindow(g_app.mainWindow) ? GetDpiForHwnd(g_app.mainWindow) : GetDpiForHwnd(nullptr);
    UiScale S{dpi};
    RECT workArea{};
    SystemParametersInfoW(SPI_GETWORKAREA, 0, &workArea, 0);
    int availableWidth = std::max(1, static_cast<int>(workArea.right - workArea.left - S(48)));
    int availableHeight = std::max(1, static_cast<int>(workArea.bottom - workArea.top - S(80)));
    int width = std::min(availableWidth, S(720));
    int height = std::min(availableHeight, S(560));
    {
        std::ostringstream log;
        log << "Codex SWBar Windows settings layout\n"
            << "dpi=" << dpi << "\n"
            << "workArea=" << workArea.left << "," << workArea.top << "," << workArea.right << "," << workArea.bottom << "\n"
            << "available=" << availableWidth << "x" << availableHeight << "\n"
            << "requested=" << width << "x" << height << "\n";
        WriteTextFileUtf8(ConfigDir() + L"\\last-settings-layout.log", log.str());
    }
    int x = workArea.left + (workArea.right - workArea.left - width) / 2;
    int y = workArea.top + (workArea.bottom - workArea.top - height) / 2;

    HWND hwnd = CreateWindowExW(
        0,
        kSettingsClass,
        L"Codex SWBar Settings",
        WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN,
        x,
        y,
        width,
        height,
        g_app.mainWindow,
        nullptr,
        g_app.instance,
        nullptr
    );
    g_app.settingsWindow = hwnd;
    ApplyFluentWindowBackdrop(hwnd, false);
    return hwnd;
}

LRESULT CALLBACK SettingsProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    switch (msg) {
        case WM_MOUSEACTIVATE:
            return MA_ACTIVATE;

        case WM_CREATE:
            ApplyWindowIcons(hwnd);
            PopulateSettingsWindow(hwnd);
            return 0;

        case WM_PAINT:
            PaintSettingsWindow(hwnd);
            return 0;

        case WM_ERASEBKGND:
            return 1;

        case WM_DRAWITEM:
            if (DrawSettingsButtonControl(reinterpret_cast<DRAWITEMSTRUCT*>(lParam))) {
                return TRUE;
            }
            break;

        case WM_CTLCOLORSTATIC: {
            HDC dc = reinterpret_cast<HDC>(wParam);
            FluentPalette palette = CurrentPalette();
            SetBkMode(dc, TRANSPARENT);
            SetTextColor(dc, palette.text);
            return reinterpret_cast<LRESULT>(GetStockObject(NULL_BRUSH));
        }

        case WM_CTLCOLOREDIT: {
            HDC dc = reinterpret_cast<HDC>(wParam);
            bool dark = IsWindowsDarkMode();
            static HBRUSH darkEditBrush = CreateSolidBrush(RGB(54, 56, 62));
            static HBRUSH lightEditBrush = CreateSolidBrush(RGB(250, 251, 253));
            SetTextColor(dc, dark ? RGB(244, 245, 247) : RGB(29, 35, 45));
            SetBkColor(dc, dark ? RGB(54, 56, 62) : RGB(250, 251, 253));
            return reinterpret_cast<LRESULT>(dark ? darkEditBrush : lightEditBrush);
        }

        case WM_CTLCOLORBTN: {
            HDC dc = reinterpret_cast<HDC>(wParam);
            FluentPalette palette = CurrentPalette();
            SetBkMode(dc, TRANSPARENT);
            SetTextColor(dc, palette.text);
            return reinterpret_cast<LRESULT>(GetStockObject(NULL_BRUSH));
        }

        case WM_SIZE:
            PopulateSettingsWindow(hwnd, true);
            InvalidateRect(hwnd, nullptr, FALSE);
            return 0;

        case WM_DPICHANGED: {
            RECT* suggested = reinterpret_cast<RECT*>(lParam);
            if (suggested) {
                SetWindowPos(
                    hwnd,
                    nullptr,
                    suggested->left,
                    suggested->top,
                    suggested->right - suggested->left,
                    suggested->bottom - suggested->top,
                    SWP_NOZORDER | SWP_NOACTIVATE
                );
            }
            PopulateSettingsWindow(hwnd, true);
            InvalidateRect(hwnd, nullptr, FALSE);
            return 0;
        }

        case WM_THEMECHANGED:
            ApplyFluentWindowBackdrop(hwnd, false);
            PopulateSettingsWindow(hwnd, true);
            InvalidateRect(hwnd, nullptr, FALSE);
            return 0;

        case WM_COMMAND: {
            int id = LOWORD(wParam);
            if (id == CONTROL_SETTINGS_REFRESH_EDIT &&
                (HIWORD(wParam) == EN_SETFOCUS || HIWORD(wParam) == EN_KILLFOCUS)) {
                InvalidateRect(hwnd, nullptr, FALSE);
                return 0;
            }
            if (id == CONTROL_SETTINGS_CLAUDE_CHECK || id == CONTROL_SETTINGS_ACRYLIC_CHECK) {
                HWND control = reinterpret_cast<HWND>(lParam);
                if (control) {
                    SetControlChecked(control, !ControlChecked(control));
                    InvalidateRect(hwnd, nullptr, FALSE);
                }
                return 0;
            }
            if (id == CONTROL_SETTINGS_SAVE) {
                SaveSettingsFromWindow(hwnd);
                return 0;
            }
            if (id == CONTROL_SETTINGS_REFRESH_NOW) {
                RefreshAsync();
                return 0;
            }
            if (id == CONTROL_SETTINGS_ADD_PROFILE) {
                AddProfileFromHud(hwnd);
                PopulateSettingsWindow(hwnd);
                return 0;
            }
            if (id == CONTROL_SETTINGS_OPEN_CONFIG) {
                OpenConfigFile(hwnd);
                return 0;
            }
            if (id == CONTROL_SETTINGS_OPEN_PROFILES) {
                OpenProfilesFolder(hwnd);
                return 0;
            }

            if (id >= CONTROL_SETTINGS_LOGIN_BASE && id <= CONTROL_SETTINGS_LOGIN_BASE + CONTROL_SETTINGS_PROFILE_LIMIT) {
                LoginCodexProfileAsync(static_cast<size_t>(id - CONTROL_SETTINGS_LOGIN_BASE));
                return 0;
            }
            if (id >= CONTROL_SETTINGS_RENAME_BASE && id <= CONTROL_SETTINGS_RENAME_BASE + CONTROL_SETTINGS_PROFILE_LIMIT) {
                RenameProfileFromHud(hwnd, static_cast<size_t>(id - CONTROL_SETTINGS_RENAME_BASE));
                PopulateSettingsWindow(hwnd);
                return 0;
            }
            if (id >= CONTROL_SETTINGS_TOGGLE_BASE && id <= CONTROL_SETTINGS_TOGGLE_BASE + CONTROL_SETTINGS_PROFILE_LIMIT) {
                ToggleProfileFromHud(hwnd, static_cast<size_t>(id - CONTROL_SETTINGS_TOGGLE_BASE));
                PopulateSettingsWindow(hwnd);
                return 0;
            }
            if (id >= CONTROL_SETTINGS_FOLDER_BASE && id <= CONTROL_SETTINGS_FOLDER_BASE + CONTROL_SETTINGS_PROFILE_LIMIT) {
                OpenProfileFolderFromHud(hwnd, static_cast<size_t>(id - CONTROL_SETTINGS_FOLDER_BASE));
                return 0;
            }
            break;
        }

        case WM_CLOSE:
            DestroyWindow(hwnd);
            return 0;

        case WM_DESTROY:
            if (g_app.settingsWindow == hwnd) g_app.settingsWindow = nullptr;
            return 0;

        default:
            return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    return DefWindowProcW(hwnd, msg, wParam, lParam);
}

std::wstring ProfilePathLabel(const std::wstring& path) {
    std::wstring appData = GetEnvVar(L"APPDATA");
    if (!appData.empty() && path.rfind(appData, 0) == 0) {
        return L"%APPDATA%" + path.substr(appData.size());
    }
    return path;
}

void PaintMainWindowDpi(HWND hwnd) {
    PAINTSTRUCT ps{};
    HDC windowDc = BeginPaint(hwnd, &ps);
    RECT client{};
    GetClientRect(hwnd, &client);
    int paintWidth = std::max(1, static_cast<int>(client.right - client.left));
    int paintHeight = std::max(1, static_cast<int>(client.bottom - client.top));
    HDC bufferDc = CreateCompatibleDC(windowDc);
    HBITMAP bufferBitmap = bufferDc ? CreateCompatibleBitmap(windowDc, paintWidth, paintHeight) : nullptr;
    HGDIOBJ oldBufferBitmap = nullptr;
    HDC dc = windowDc;
    if (bufferDc && bufferBitmap) {
        oldBufferBitmap = SelectObject(bufferDc, bufferBitmap);
        dc = bufferDc;
    }

    UiScale S{GetDpiForHwnd(hwnd)};
    FluentPalette palette = CurrentPalette();
    g_app.hitTargets.clear();

    HFONT titleFont = CreateFontW(S(24), 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                                  OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                                  DEFAULT_PITCH, L"Segoe UI Variable Display");
    HFONT sectionFont = CreateFontW(S(16), 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                                    OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                                    DEFAULT_PITCH, L"Segoe UI Variable");
    HFONT bodyFont = CreateFontW(S(14), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                                 OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                                 DEFAULT_PITCH, L"Segoe UI Variable");
    HFONT smallFont = CreateFontW(S(12), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                                  OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                                  DEFAULT_PITCH, L"Segoe UI");
    HFONT dataFont = CreateFontW(S(14), 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                                 OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                                 DEFAULT_PITCH, L"Segoe UI Variable");

    FillRectColor(dc, client, palette.page);

    struct TopButton {
        UiAction action;
        std::wstring text;
        int width;
        bool primary;
        bool disabled;
    };

    std::vector<std::pair<TopButton, RECT>> topButtons;
    std::vector<TopButton> topButtonDefs{
        {UiAction::Refresh, g_app.refreshing ? L"Refreshing" : L"Refresh", 118, true, g_app.refreshing},
        {UiAction::EditConfig, L"Settings", 108, false, false},
        {UiAction::AddProfile, L"New profile", 130, false, false},
        {UiAction::OpenConfig, L"Config file", 116, false, false},
        {UiAction::OpenProfiles, L"Profiles", 102, false, false}
    };

    int buttonH = S(36);
    int buttonX = S(24);
    int buttonY = S(84);
    int rightLimit = std::max<int>(S(360), static_cast<int>(client.right) - S(24));
    for (const auto& def : topButtonDefs) {
        int width = S(def.width);
        if (buttonX + width > rightLimit && buttonX > S(24)) {
            buttonX = S(24);
            buttonY += buttonH + S(8);
        }
        RECT rect{buttonX, buttonY, buttonX + width, buttonY + buttonH};
        topButtons.push_back({def, rect});
        buttonX += width + S(10);
    }

    int headerHeight = std::max(S(132), buttonY + buttonH + S(14));
    RECT header{0, 0, client.right, headerHeight};
    FillRectColor(dc, header, palette.surface);
    RECT headerLine{0, headerHeight - S(1), client.right, headerHeight};
    FillRectColor(dc, headerLine, palette.border);

    int titleLeft = S(22);
    if (g_app.icon) {
        DrawIconEx(dc, S(22), S(16), g_app.icon, S(38), S(38), 0, nullptr, DI_NORMAL);
        titleLeft = S(72);
    }
    RECT titleRect{titleLeft, S(13), client.right - S(24), S(44)};
    DrawTextLine(dc, kAppTitle, titleRect, palette.text, titleFont, DT_LEFT | DT_VCENTER | DT_SINGLELINE);
    RECT subtitleRect{titleLeft + S(2), S(45), client.right - S(24), S(68)};
    DrawTextLine(dc, L"Settings for taskbar presence, isolated profiles, quotas, and login", subtitleRect, palette.muted, smallFont, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS);

    for (const auto& [def, rect] : topButtons) {
        DrawButton(
            dc,
            rect,
            def.text,
            bodyFont,
            def.primary,
            def.disabled,
            SameTarget(g_app.hoverTarget, def.action, -1),
            SameTarget(g_app.pressedTarget, def.action, -1)
        );
        if (!def.disabled) AddHitTarget(rect, def.action);
    }

    int sidebarWidth = S(248);
    RECT sidebar{0, headerHeight, sidebarWidth, client.bottom};
    FillRectColor(dc, sidebar, palette.surfaceAlt);
    RECT sidebarLine{sidebarWidth - S(1), headerHeight, sidebarWidth, client.bottom};
    FillRectColor(dc, sidebarLine, palette.border);

    std::vector<UsageRow> rows;
    AppConfig config;
    {
        std::lock_guard<std::mutex> lock(g_app.rowsMutex);
        rows = g_app.rows;
        config = g_app.config;
    }

    RECT navTitle{S(20), headerHeight + S(20), sidebarWidth - S(20), headerHeight + S(42)};
    DrawTextLine(dc, L"Codex profiles", navTitle, palette.muted, smallFont, DT_LEFT | DT_VCENTER | DT_SINGLELINE);
    int navY = headerHeight + S(54);
    for (size_t i = 0; i < config.codexProfiles.size(); ++i) {
        const auto& profile = config.codexProfiles[i];
        int profileIndex = static_cast<int>(i);
        RECT item{S(14), navY, sidebarWidth - S(14), navY + S(124)};
        DrawRoundRectColor(dc, item, S(8), profile.enabled ? palette.surface : palette.controlPressed, palette.border);
        RECT labelRect{S(26), navY + S(8), sidebarWidth - S(26), navY + S(30)};
        DrawTextLine(dc, profile.label, labelRect, profile.enabled ? palette.text : palette.subtle, bodyFont, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS);
        RECT pathRect{S(26), navY + S(31), sidebarWidth - S(26), navY + S(52)};
        DrawTextLine(dc, ProfilePathLabel(profile.codexHome), pathRect, palette.muted, smallFont, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS);

        RECT loginProfileButton{S(26), navY + S(62), S(92), navY + S(88)};
        RECT renameProfileButton{S(98), navY + S(62), S(178), navY + S(88)};
        RECT toggleProfileButton{S(26), navY + S(92), S(112), navY + S(118)};
        RECT folderProfileButton{S(118), navY + S(92), S(198), navY + S(118)};
        DrawButton(dc, loginProfileButton, L"Login", smallFont, false, g_app.loggingIn, SameTarget(g_app.hoverTarget, UiAction::LoginProfile, profileIndex), SameTarget(g_app.pressedTarget, UiAction::LoginProfile, profileIndex));
        DrawButton(dc, renameProfileButton, L"Rename", smallFont, false, false, SameTarget(g_app.hoverTarget, UiAction::RenameProfile, profileIndex), SameTarget(g_app.pressedTarget, UiAction::RenameProfile, profileIndex));
        DrawButton(dc, toggleProfileButton, profile.enabled ? L"Disable" : L"Enable", smallFont, false, false, SameTarget(g_app.hoverTarget, UiAction::ToggleProfile, profileIndex), SameTarget(g_app.pressedTarget, UiAction::ToggleProfile, profileIndex));
        DrawButton(dc, folderProfileButton, L"Folder", smallFont, false, false, SameTarget(g_app.hoverTarget, UiAction::OpenProfileFolder, profileIndex), SameTarget(g_app.pressedTarget, UiAction::OpenProfileFolder, profileIndex));
        if (!g_app.loggingIn) AddHitTarget(loginProfileButton, UiAction::LoginProfile, profileIndex);
        AddHitTarget(renameProfileButton, UiAction::RenameProfile, profileIndex);
        AddHitTarget(toggleProfileButton, UiAction::ToggleProfile, profileIndex);
        AddHitTarget(folderProfileButton, UiAction::OpenProfileFolder, profileIndex);
        navY += S(136);
    }

    int contentLeft = sidebarWidth + S(28);
    int contentRight = std::max<int>(contentLeft + S(320), static_cast<int>(client.right) - S(28));
    RECT contentTitle{contentLeft, headerHeight + S(24), contentRight, headerHeight + S(52)};
    DrawTextLine(dc, L"Status", contentTitle, palette.text, sectionFont, DT_LEFT | DT_VCENTER | DT_SINGLELINE);

    std::wstring observer = L"Auto-refresh: " + std::to_wstring(config.refreshIntervalSeconds) + L"s";
    if (g_app.loggingIn) observer += L" / login in progress";
    RECT observerRect{std::max(contentLeft + S(160), contentRight - S(320)), headerHeight + S(24), contentRight, headerHeight + S(52)};
    DrawTextLine(dc, observer, observerRect, palette.muted, smallFont, DT_RIGHT | DT_VCENTER | DT_SINGLELINE);

    int rowY = headerHeight + S(64);
    if (rows.empty()) {
        RECT empty{contentLeft, rowY, contentRight, rowY + S(104)};
        DrawRoundRectColor(dc, empty, S(8), palette.surface, palette.border);
        RECT emptyText{contentLeft + S(18), rowY + S(20), contentRight - S(18), rowY + S(48)};
        DrawTextLine(dc, L"Refresh in progress", emptyText, palette.text, sectionFont, DT_LEFT | DT_VCENTER | DT_SINGLELINE);
        RECT emptySub{contentLeft + S(18), rowY + S(50), contentRight - S(18), rowY + S(78)};
        DrawTextLine(dc, L"Providers appear here when the first refresh completes.", emptySub, palette.muted, bodyFont, DT_LEFT | DT_VCENTER | DT_SINGLELINE);
    }

    for (const auto& row : rows) {
        RECT panel{contentLeft, rowY, contentRight, rowY + S(142)};
        DrawRoundRectColor(dc, panel, S(8), palette.surface, palette.border);

        RECT providerRect{contentLeft + S(18), rowY + S(12), panel.right - S(220), rowY + S(40)};
        std::wstring provider = row.provider + L" / " + row.label;
        DrawTextLine(dc, provider, providerRect, palette.text, sectionFont, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS);

        RECT badge{panel.right - S(192), rowY + S(14), panel.right - S(72), rowY + S(40)};
        DrawStatusBadge(dc, badge, row.status, smallFont);

        RECT identityRect{contentLeft + S(18), rowY + S(44), panel.right - S(230), rowY + S(68)};
        std::wstring identity = row.identity.empty() ? L"Not connected yet" : row.identity;
        if (!row.plan.empty()) identity += L" / " + row.plan;
        DrawTextLine(dc, identity, identityRect, palette.muted, bodyFont, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS);

        RECT primaryLabel{contentLeft + S(18), rowY + S(76), contentLeft + S(100), rowY + S(98)};
        DrawTextLine(dc, L"Primary", primaryLabel, palette.muted, smallFont, DT_LEFT | DT_VCENTER | DT_SINGLELINE);
        RECT primaryBar{contentLeft + S(100), rowY + S(82), panel.right - S(240), rowY + S(94)};
        DrawQuotaBar(dc, primaryBar, row.primaryPercent, palette.accent);
        RECT primaryValue{panel.right - S(226), rowY + S(74), panel.right - S(176), rowY + S(100)};
        DrawTextLine(dc, row.primaryPercent >= 0 ? std::to_wstring(row.primaryPercent) + L"%" : L"--", primaryValue, palette.text, dataFont, DT_RIGHT | DT_VCENTER | DT_SINGLELINE);

        RECT secondaryLabel{contentLeft + S(18), rowY + S(100), contentLeft + S(100), rowY + S(122)};
        DrawTextLine(dc, L"Secondary", secondaryLabel, palette.muted, smallFont, DT_LEFT | DT_VCENTER | DT_SINGLELINE);
        RECT secondaryBar{contentLeft + S(100), rowY + S(106), panel.right - S(240), rowY + S(118)};
        DrawQuotaBar(dc, secondaryBar, row.secondaryPercent, palette.success);
        RECT secondaryValue{panel.right - S(226), rowY + S(98), panel.right - S(176), rowY + S(124)};
        DrawTextLine(dc, row.secondaryPercent >= 0 ? std::to_wstring(row.secondaryPercent) + L"%" : L"--", secondaryValue, palette.text, dataFont, DT_RIGHT | DT_VCENTER | DT_SINGLELINE);

        if (!row.error.empty()) {
            RECT note{contentLeft + S(18), rowY + S(122), panel.right - S(210), rowY + S(140)};
            DrawTextLine(dc, row.error, note, palette.danger, smallFont, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS);
        }

        rowY += S(158);
    }

    RECT footer{contentLeft, client.bottom - S(42), contentRight, client.bottom - S(16)};
    DrawTextLine(dc, L"Tray menu mirrors the quick actions. Each Codex profile keeps tokens in its own CODEX_HOME.", footer, palette.muted, smallFont, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS);

    DeleteObject(titleFont);
    DeleteObject(sectionFont);
    DeleteObject(bodyFont);
    DeleteObject(smallFont);
    DeleteObject(dataFont);

    if (dc != windowDc) {
        BitBlt(windowDc, 0, 0, paintWidth, paintHeight, dc, 0, 0, SRCCOPY);
    }
    if (oldBufferBitmap) SelectObject(bufferDc, oldBufferBitmap);
    if (bufferBitmap) DeleteObject(bufferBitmap);
    if (bufferDc) DeleteDC(bufferDc);
    EndPaint(hwnd, &ps);
}

void PaintMainWindow(HWND hwnd) {
    PaintMainWindowDpi(hwnd);
}

std::wstring BuildTrayTooltip() {
    std::lock_guard<std::mutex> lock(g_app.rowsMutex);
    if (g_app.rows.empty()) return L"Codex SWBar Windows - refreshing";
    std::wstring text = kAppTitle;
    for (const auto& row : g_app.rows) {
        text += L"\n" + row.provider + L" " + row.label + L": " + row.status;
        if (row.primaryPercent >= 0) text += L" " + std::to_wstring(row.primaryPercent) + L"%";
    }
    return text.substr(0, 127);
}

void UpdateTray() {
    std::wstring tooltip = BuildTrayTooltip();
    wcsncpy(g_app.tray.szTip, tooltip.c_str(), ARRAYSIZE(g_app.tray.szTip) - 1);
    g_app.tray.szTip[ARRAYSIZE(g_app.tray.szTip) - 1] = L'\0';
    Shell_NotifyIconW(NIM_MODIFY, &g_app.tray);
}

void ShowSettingsWindow() {
    HWND hwnd = EnsureSettingsWindow();
    if (!hwnd) return;
    PopulateSettingsWindow(hwnd);
    ShowWindow(hwnd, SW_SHOWNORMAL);
    SetForegroundWindow(hwnd);
    InvalidateRect(hwnd, nullptr, FALSE);
}

void OpenConfigFile(HWND hwnd) {
    std::wstring path = ConfigPath();
    ShellExecuteW(hwnd, L"open", L"notepad.exe", QuoteForCmd(path).c_str(), nullptr, SW_SHOWNORMAL);
}

void OpenProfilesFolder(HWND hwnd) {
    EnsureDirectory(ConfigDir() + L"\\profiles");
    ShellExecuteW(hwnd, L"open", (ConfigDir() + L"\\profiles").c_str(), nullptr, nullptr, SW_SHOWNORMAL);
}

void InvokeUiAction(HWND hwnd, UiAction action, int profileIndex = -1) {
    switch (action) {
        case UiAction::Refresh:
            RefreshAsync();
            break;
        case UiAction::LoginProfile:
            if (profileIndex >= 0) LoginCodexProfileAsync(static_cast<size_t>(profileIndex));
            break;
        case UiAction::RenameProfile:
            if (profileIndex >= 0) RenameProfileFromHud(hwnd, static_cast<size_t>(profileIndex));
            break;
        case UiAction::ToggleProfile:
            if (profileIndex >= 0) ToggleProfileFromHud(hwnd, static_cast<size_t>(profileIndex));
            break;
        case UiAction::OpenProfileFolder:
            if (profileIndex >= 0) OpenProfileFolderFromHud(hwnd, static_cast<size_t>(profileIndex));
            break;
        case UiAction::AddProfile:
            AddProfileFromHud(hwnd);
            break;
        case UiAction::EditConfig:
            ShowSettingsWindow();
            break;
        case UiAction::OpenConfig:
            OpenConfigFile(hwnd);
            break;
        case UiAction::OpenProfiles:
            OpenProfilesFolder(hwnd);
            break;
        case UiAction::Exit:
            DestroyWindow(hwnd);
            break;
    }
}

void RefreshAsync() {
    if (g_app.shuttingDown) return;
    bool expected = false;
    if (!g_app.refreshing.compare_exchange_strong(expected, true)) {
        g_app.refreshPending = true;
        return;
    }
    g_app.refreshPending = false;

    if (g_app.refreshThread.joinable()) {
        g_app.refreshThread.join();
    }

    g_app.refreshThread = std::thread([] {
        AppConfig config = LoadConfig();
        std::vector<UsageRow> nextRows;

        for (size_t i = 0; i < config.codexProfiles.size(); ++i) {
            const auto& profile = config.codexProfiles[i];
            if (!profile.enabled) continue;
            UsageRow row = FetchCodexProfile(profile, &g_app.shuttingDown);
            row.profileIndex = static_cast<int>(i);
            nextRows.push_back(row);
        }
        if (config.claude.enabled) {
            nextRows.push_back(FetchClaude(config.claude, &g_app.shuttingDown));
        }

        WriteRefreshLog(nextRows);

        if (g_app.shuttingDown) {
            g_app.refreshing = false;
            return;
        }

        AppConfig latestConfig = LoadConfig();
        {
            std::lock_guard<std::mutex> lock(g_app.rowsMutex);
            g_app.config = latestConfig;
            g_app.rows = nextRows;
            GetLocalTime(&g_app.lastRefreshLocal);
            g_app.hasLastRefresh = true;
        }

        g_app.refreshing = false;
        if (!g_app.shuttingDown) {
            PostMessageW(g_app.mainWindow, WM_REFRESH_DONE, 0, 0);
            if (g_app.refreshPending.exchange(false)) {
                PostMessageW(g_app.mainWindow, WM_REFRESH_REQUEST, 0, 0);
            }
        }
    });
}

struct ContextMenuItem {
    std::wstring label;
    const wchar_t* glyph = nullptr;
    UiAction action = UiAction::Refresh;
    int profileIndex = -1;
    bool separator = false;
    bool disabled = false;
};

std::vector<ContextMenuItem> BuildContextMenuItems() {
    std::vector<ContextMenuItem> items;
    AppConfig config = CurrentConfigSnapshot();
    items.push_back({L"Refresh now", kGlyphRefresh, UiAction::Refresh});
    items.push_back({L"Settings", kGlyphSettings, UiAction::EditConfig});
    items.push_back({L"New profile", kGlyphAdd, UiAction::AddProfile});

    if (!config.codexProfiles.empty()) {
        items.push_back(ContextMenuItem{L"", nullptr, UiAction::Refresh, -1, true});
        size_t count = std::min<size_t>(config.codexProfiles.size(), 6);
        for (size_t i = 0; i < count; ++i) {
            items.push_back({L"Login " + config.codexProfiles[i].label, kGlyphPerson, UiAction::LoginProfile, static_cast<int>(i), false, g_app.loggingIn.load()});
        }
    }

    items.push_back(ContextMenuItem{L"", nullptr, UiAction::Refresh, -1, true});
    items.push_back({L"Open config", kGlyphDocument, UiAction::OpenConfig});
    items.push_back({L"Profiles folder", kGlyphFolder, UiAction::OpenProfiles});
    items.push_back(ContextMenuItem{L"", nullptr, UiAction::Refresh, -1, true});
    items.push_back({L"Exit", kGlyphPower, UiAction::Exit});
    return items;
}

SIZE ContextMenuSize(UINT dpi) {
    UiScale S{dpi};
    int height = S(12);
    for (const auto& item : BuildContextMenuItems()) {
        height += item.separator ? S(9) : S(34);
    }
    height += S(8);
    return SIZE{S(236), height};
}

TargetKey ContextMenuTargetAtPoint(HWND hwnd, POINT point) {
    RECT client{};
    GetClientRect(hwnd, &client);
    UiScale S{GetDpiForHwnd(hwnd)};
    int y = S(8);
    for (const auto& item : BuildContextMenuItems()) {
        if (item.separator) {
            y += S(9);
            continue;
        }
        RECT rect{S(6), y, client.right - S(6), y + S(34)};
        if (!item.disabled && PtInRect(&rect, point)) {
            return TargetKey{true, item.action, item.profileIndex};
        }
        y += S(34);
    }
    return TargetKey{};
}

void PaintContextMenu(HWND hwnd) {
    PAINTSTRUCT ps{};
    HDC windowDc = BeginPaint(hwnd, &ps);
    RECT client{};
    GetClientRect(hwnd, &client);
    int width = std::max(1, RectWidth(client));
    int height = std::max(1, RectHeight(client));

    HDC bufferDc = CreateCompatibleDC(windowDc);
    HBITMAP bitmap = bufferDc ? CreateCompatibleBitmap(windowDc, width, height) : nullptr;
    HGDIOBJ oldBitmap = nullptr;
    HDC dc = windowDc;
    if (bufferDc && bitmap) {
        oldBitmap = SelectObject(bufferDc, bitmap);
        dc = bufferDc;
    }

    UINT dpi = GetDpiForHwnd(hwnd);
    UiScale S{dpi};
    FluentPalette palette = CurrentPalette();
    COLORREF base = FlyoutBaseColor(palette.dark);

    FillRectColor(dc, client, base);
    RECT panel = client;
    InflateRect(&panel, -S(1), -S(1));
    DrawRoundRectOutline(dc, panel, S(12), palette.borderStrong);

    HFONT itemFont = CreateFontW(S(13), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                                 OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                                 DEFAULT_PITCH, L"Segoe UI Variable");
    HFONT iconFont = CreateFontW(S(15), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                                 OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                                 DEFAULT_PITCH, L"Segoe Fluent Icons");

    int y = S(8);
    for (const auto& item : BuildContextMenuItems()) {
        if (item.separator) {
            RECT line{S(12), y + S(4), client.right - S(12), y + S(5)};
            FillRectColor(dc, line, palette.border);
            y += S(9);
            continue;
        }

        RECT row{S(6), y, client.right - S(6), y + S(34)};
        bool hovered = SameTarget(g_app.contextMenuHoverTarget, item.action, item.profileIndex);
        bool pressed = SameTarget(g_app.contextMenuPressedTarget, item.action, item.profileIndex);
        if (!item.disabled && (hovered || pressed)) {
            DrawRoundRectColor(dc, row, S(6), pressed ? palette.controlPressed : palette.controlHover, pressed ? palette.borderStrong : palette.border);
        }

        COLORREF ink = item.disabled ? palette.subtle : palette.text;
        RECT glyphRect{row.left + S(10), row.top, row.left + S(30), row.bottom};
        if (item.glyph) {
            DrawTextLine(dc, item.glyph, glyphRect, item.disabled ? palette.subtle : palette.muted, iconFont, DT_CENTER | DT_VCENTER | DT_SINGLELINE);
        }
        RECT labelRect{row.left + S(38), row.top, row.right - S(12), row.bottom};
        DrawTextLine(dc, item.label, labelRect, ink, itemFont, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS);
        y += S(34);
    }

    DeleteObject(itemFont);
    DeleteObject(iconFont);

    if (dc != windowDc) {
        BitBlt(windowDc, 0, 0, width, height, dc, 0, 0, SRCCOPY);
    }
    if (oldBitmap) SelectObject(bufferDc, oldBitmap);
    if (bitmap) DeleteObject(bitmap);
    if (bufferDc) DeleteDC(bufferDc);
    EndPaint(hwnd, &ps);
}

void HideContextMenu() {
    HWND hwnd = g_app.contextMenuWindow;
    if (hwnd && IsWindow(hwnd)) {
        ShowWindow(hwnd, SW_HIDE);
        if (GetCapture() == hwnd) ReleaseCapture();
    }
    g_app.contextMenuHoverTarget = TargetKey{};
    g_app.contextMenuPressedTarget = TargetKey{};
}

HWND EnsureContextMenuWindow() {
    if (g_app.contextMenuWindow && IsWindow(g_app.contextMenuWindow)) {
        return g_app.contextMenuWindow;
    }

    HWND hwnd = CreateWindowExW(
        WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_LAYERED | WS_EX_NOACTIVATE,
        kContextMenuClass,
        L"Codex SWBar menu",
        WS_POPUP,
        CW_USEDEFAULT,
        CW_USEDEFAULT,
        1,
        1,
        g_app.mainWindow,
        nullptr,
        g_app.instance,
        nullptr
    );
    g_app.contextMenuWindow = hwnd;
    ConfigureFlyoutMaterial(hwnd);
    return hwnd;
}

void ShowContextMenu(HWND hwnd) {
    POINT pt{};
    GetCursorPos(&pt);
    HWND menu = EnsureContextMenuWindow();
    if (!menu) return;
    ConfigureFlyoutMaterial(menu);

    UINT dpi = GetDpiForHwnd(menu);
    SIZE size = ContextMenuSize(dpi);
    HMONITOR monitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
    MONITORINFO mi{};
    mi.cbSize = sizeof(mi);
    GetMonitorInfoW(monitor, &mi);
    RECT workArea = IsMeaningfulRect(mi.rcWork) ? mi.rcWork : mi.rcMonitor;
    RECT target{pt.x, pt.y, pt.x + size.cx, pt.y + size.cy};
    target = ClampRectToWorkArea(target, workArea);

    g_app.contextMenuHoverTarget = TargetKey{};
    g_app.contextMenuPressedTarget = TargetKey{};
    HideCodexBarFlyout();
    SetWindowPos(menu, HWND_TOPMOST, target.left, target.top, size.cx, size.cy, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    SetCapture(menu);
    InvalidateRect(menu, nullptr, FALSE);
    UpdateWindow(menu);
    (void)hwnd;
}

LRESULT CALLBACK ContextMenuProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    auto commandOwner = []() {
        return g_app.mainWindow ? g_app.mainWindow : g_app.contextMenuWindow;
    };
    auto targetAt = [&](LPARAM pointParam) {
        POINT point{GET_X_LPARAM(pointParam), GET_Y_LPARAM(pointParam)};
        return ContextMenuTargetAtPoint(hwnd, point);
    };

    switch (msg) {
        case WM_PAINT:
            PaintContextMenu(hwnd);
            return 0;

        case WM_ERASEBKGND:
            return 1;

        case WM_KEYDOWN:
            if (wParam == VK_ESCAPE) {
                HideContextMenu();
                return 0;
            }
            break;

        case WM_MOUSEMOVE: {
            TargetKey next = targetAt(lParam);
            if (next.valid) SetCursor(LoadCursorW(nullptr, IDC_HAND));
            bool changed = g_app.contextMenuHoverTarget.valid != next.valid ||
                           g_app.contextMenuHoverTarget.action != next.action ||
                           g_app.contextMenuHoverTarget.profileIndex != next.profileIndex;
            if (changed) {
                g_app.contextMenuHoverTarget = next;
                InvalidateRect(hwnd, nullptr, FALSE);
            }
            return 0;
        }

        case WM_LBUTTONDOWN:
        case WM_RBUTTONDOWN: {
            TargetKey target = targetAt(lParam);
            if (!target.valid) {
                HideContextMenu();
                return 0;
            }
            g_app.contextMenuPressedTarget = target;
            SetCapture(hwnd);
            InvalidateRect(hwnd, nullptr, FALSE);
            return 0;
        }

        case WM_LBUTTONUP:
        case WM_RBUTTONUP: {
            TargetKey released = targetAt(lParam);
            TargetKey pressed = g_app.contextMenuPressedTarget;
            g_app.contextMenuPressedTarget = TargetKey{};
            InvalidateRect(hwnd, nullptr, FALSE);
            if (!released.valid) {
                HideContextMenu();
                return 0;
            }
            if (pressed.valid && pressed.action == released.action && pressed.profileIndex == released.profileIndex) {
                UiAction action = released.action;
                int profileIndex = released.profileIndex;
                HideContextMenu();
                InvokeUiAction(commandOwner(), action, profileIndex);
            }
            return 0;
        }

        case WM_CAPTURECHANGED:
            if (IsWindowVisible(hwnd) && reinterpret_cast<HWND>(lParam) != hwnd) {
                HideContextMenu();
            }
            return 0;

        case WM_NCDESTROY:
            if (g_app.contextMenuWindow == hwnd) g_app.contextMenuWindow = nullptr;
            return 0;

        default:
            return DefWindowProcW(hwnd, msg, wParam, lParam);
    }
    return DefWindowProcW(hwnd, msg, wParam, lParam);
}

void AddTrayIcon(HWND hwnd) {
    LoadAppIconsForDpi(GetDpiForHwnd(hwnd));
    HICON trayIcon = g_app.smallIcon ? g_app.smallIcon : (g_app.icon ? g_app.icon : LoadIconW(nullptr, IDI_APPLICATION));
    g_app.tray = {};
    g_app.tray.cbSize = sizeof(g_app.tray);
    g_app.tray.hWnd = hwnd;
    g_app.tray.uID = TRAY_ID;
    g_app.tray.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
    g_app.tray.uCallbackMessage = WM_TRAYICON;
    g_app.tray.hIcon = trayIcon;
    wcscpy(g_app.tray.szTip, kAppTitle);
    Shell_NotifyIconW(NIM_ADD, &g_app.tray);
}

void RemoveTrayIcon() {
    Shell_NotifyIconW(NIM_DELETE, &g_app.tray);
}

void EnableDpiAwareness() {
    HMODULE user32 = GetModuleHandleW(L"user32.dll");
    if (!user32) return;
    using SetProcessDpiAwarenessContextFn = BOOL (WINAPI*)(HANDLE);
    using SetProcessDPIAwareFn = BOOL (WINAPI*)();
#if defined(__GNUC__)
#pragma GCC diagnostic push
#pragma GCC diagnostic ignored "-Wcast-function-type"
#endif
    auto setProcessDpiAwarenessContext = reinterpret_cast<SetProcessDpiAwarenessContextFn>(GetProcAddress(user32, "SetProcessDpiAwarenessContext"));
    auto setProcessDpiAware = reinterpret_cast<SetProcessDPIAwareFn>(GetProcAddress(user32, "SetProcessDPIAware"));
#if defined(__GNUC__)
#pragma GCC diagnostic pop
#endif
    if (setProcessDpiAwarenessContext && setProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)) return;
    if (setProcessDpiAware) setProcessDpiAware();
}

LRESULT CALLBACK MainProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    if (g_taskbarCreatedMessage != 0 && msg == g_taskbarCreatedMessage) {
        AddTrayIcon(hwnd);
        RecreateTaskbarPresence(hwnd);
        return 0;
    }

    switch (msg) {
        case WM_CREATE:
            g_app.mainWindow = hwnd;
            ApplyWindowIcons(hwnd);
            AddTrayIcon(hwnd);
            g_app.config = LoadConfig();
            g_app.activeRefreshIntervalSeconds = g_app.config.refreshIntervalSeconds;
            SetTimer(hwnd, TIMER_REFRESH, static_cast<UINT>(g_app.activeRefreshIntervalSeconds) * 1000u, nullptr);
            SetTimer(hwnd, TIMER_TASKBAR_REPOSITION, 1500u, nullptr);
            RecreateTaskbarPresence(hwnd);
            RefreshAsync();
            return 0;

        case WM_TIMER:
            if (wParam == TIMER_REFRESH) RefreshAsync();
            if (wParam == TIMER_TASKBAR_REPOSITION) PositionTaskbarPresence();
            if (wParam == TIMER_FLYOUT_WATCHDOG) UpdateCodexBarFlyoutWatchdog();
            return 0;

        case WM_TRAYICON:
            if (LOWORD(lParam) == WM_LBUTTONUP) {
                UpdateTaskbarAnchorFromCursor();
                ToggleCodexBarFlyout();
            } else if (LOWORD(lParam) == WM_RBUTTONUP) {
                UpdateTaskbarAnchorFromCursor();
                ShowContextMenu(hwnd);
            }
            return 0;

        case WM_SHOW_SETTINGS:
            ShowSettingsWindow();
            return 0;

        case WM_PAINT:
            PaintMainWindow(hwnd);
            return 0;

        case WM_ERASEBKGND:
            return 1;

        case WM_SIZE:
            ClearInteractiveTargets(hwnd);
            InvalidateRect(hwnd, nullptr, FALSE);
            return 0;

        case WM_DPICHANGED: {
            RECT* suggested = reinterpret_cast<RECT*>(lParam);
            if (suggested) {
                SetWindowPos(
                    hwnd,
                    nullptr,
                    suggested->left,
                    suggested->top,
                    suggested->right - suggested->left,
                    suggested->bottom - suggested->top,
                    SWP_NOZORDER | SWP_NOACTIVATE
                );
            }
            ClearInteractiveTargets(hwnd);
            ApplyWindowIcons(hwnd);
            UpdateTaskbarPresence();
            InvalidateRect(hwnd, nullptr, FALSE);
            return 0;
        }

        case WM_DISPLAYCHANGE:
        case WM_SETTINGCHANGE:
            EnsureTaskbarPresenceTopology(hwnd);
            if (g_app.codexBarFlyoutWindow && IsWindow(g_app.codexBarFlyoutWindow)) {
                ApplyFluentWindowBackdrop(g_app.codexBarFlyoutWindow, true);
            }
            if (g_app.contextMenuWindow && IsWindow(g_app.contextMenuWindow)) {
                ApplyFluentWindowBackdrop(g_app.contextMenuWindow, true);
            }
            if (g_app.settingsWindow && IsWindow(g_app.settingsWindow)) {
                ApplyFluentWindowBackdrop(g_app.settingsWindow, false);
            }
            if (g_app.codexBarFlyoutWindow && IsWindowVisible(g_app.codexBarFlyoutWindow)) {
                PositionCodexBarFlyout(g_app.codexBarFlyoutWindow);
                InvalidateRect(g_app.codexBarFlyoutWindow, nullptr, FALSE);
            }
            return 0;

        case WM_THEMECHANGED:
            if (g_app.codexBarFlyoutWindow && IsWindow(g_app.codexBarFlyoutWindow)) {
                ApplyFluentWindowBackdrop(g_app.codexBarFlyoutWindow, true);
            }
            if (g_app.contextMenuWindow && IsWindow(g_app.contextMenuWindow)) {
                ApplyFluentWindowBackdrop(g_app.contextMenuWindow, true);
            }
            if (g_app.settingsWindow && IsWindow(g_app.settingsWindow)) {
                ApplyFluentWindowBackdrop(g_app.settingsWindow, false);
            }
            PositionTaskbarPresence();
            if (g_app.codexBarFlyoutWindow && IsWindowVisible(g_app.codexBarFlyoutWindow)) {
                PositionCodexBarFlyout(g_app.codexBarFlyoutWindow);
                InvalidateRect(g_app.codexBarFlyoutWindow, nullptr, FALSE);
            }
            return 0;

        case WM_MOUSEMOVE: {
            POINT point{GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam)};
            bool overTarget = UpdateHoverTarget(hwnd, point);
            if (overTarget && !g_app.trackingMouseLeave) {
                TRACKMOUSEEVENT event{};
                event.cbSize = sizeof(event);
                event.dwFlags = TME_LEAVE;
                event.hwndTrack = hwnd;
                if (TrackMouseEvent(&event)) g_app.trackingMouseLeave = true;
            }
            return 0;
        }

        case WM_MOUSELEAVE:
            ClearInteractiveTargets(hwnd);
            return 0;

        case WM_LBUTTONDOWN: {
            POINT point{GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam)};
            HitTarget hit;
            if (HitTargetAtPoint(point, hit)) {
                g_app.pressedTarget = TargetKey{true, hit.action, hit.profileIndex};
                SetCapture(hwnd);
                InvalidateRect(hwnd, nullptr, FALSE);
            }
            return 0;
        }

        case WM_LBUTTONUP: {
            POINT point{GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam)};
            HitTarget hit;
            bool hasPressed = g_app.pressedTarget.valid;
            TargetKey pressed = g_app.pressedTarget;
            g_app.pressedTarget = TargetKey{};
            if (GetCapture() == hwnd) ReleaseCapture();
            UpdateHoverTarget(hwnd, point);
            if (hasPressed && HitTargetAtPoint(point, hit) &&
                pressed.action == hit.action && pressed.profileIndex == hit.profileIndex) {
                InvokeUiAction(hwnd, hit.action, hit.profileIndex);
                return 0;
            }
            InvalidateRect(hwnd, nullptr, FALSE);
            return 0;
        }

        case WM_COMMAND:
            if (LOWORD(wParam) >= MENU_LOGIN_PROFILE_BASE && LOWORD(wParam) <= MENU_LOGIN_PROFILE_LIMIT) {
                size_t profileIndex = static_cast<size_t>(LOWORD(wParam) - MENU_LOGIN_PROFILE_BASE);
                if (profileIndex < g_app.menuLoginProfiles.size()) {
                    LoginCodexProfileConfigAsync(g_app.menuLoginProfiles[profileIndex]);
                }
                return 0;
            }

            switch (LOWORD(wParam)) {
                case MENU_REFRESH:
                    RefreshAsync();
                    return 0;
                case MENU_EDIT_CONFIG:
                    ShowSettingsWindow();
                    return 0;
                case MENU_ADD_PROFILE:
                    AddProfileFromHud(hwnd);
                    return 0;
                case MENU_OPEN_CONFIG: {
                    OpenConfigFile(hwnd);
                    return 0;
                }
                case MENU_OPEN_PROFILES:
                    OpenProfilesFolder(hwnd);
                    return 0;
                case MENU_EXIT:
                    DestroyWindow(hwnd);
                    return 0;
                default:
                    break;
            }
            return 0;

        case WM_CLOSE:
            ShowWindow(hwnd, SW_HIDE);
            return 0;

        case WM_REFRESH_DONE:
            UpdateTray();
            UpdateTaskbarPresence();
            {
                int refreshIntervalSeconds = 300;
                {
                    std::lock_guard<std::mutex> lock(g_app.rowsMutex);
                    refreshIntervalSeconds = g_app.config.refreshIntervalSeconds;
                }
                if (refreshIntervalSeconds != g_app.activeRefreshIntervalSeconds) {
                    g_app.activeRefreshIntervalSeconds = refreshIntervalSeconds;
                    SetTimer(hwnd, TIMER_REFRESH, static_cast<UINT>(g_app.activeRefreshIntervalSeconds) * 1000u, nullptr);
                }
            }
            InvalidateRect(hwnd, nullptr, FALSE);
            return 0;

        case WM_REFRESH_REQUEST:
            RefreshAsync();
            return 0;

        case WM_LOGIN_DONE: {
            LoginNotice notice;
            bool hasNotice = false;
            {
                std::lock_guard<std::mutex> lock(g_app.uiQueueMutex);
                if (!g_app.pendingLoginNotices.empty()) {
                    notice = g_app.pendingLoginNotices.front();
                    g_app.pendingLoginNotices.pop_front();
                    hasNotice = true;
                }
            }
            MessageBoxW(
                hwnd,
                hasNotice ? notice.message.c_str() : L"Codex login finished.",
                hasNotice && notice.informational ? L"Codex login code" : (hasNotice && notice.success ? L"Codex login complete" : L"Codex login"),
                MB_OK | (hasNotice && (notice.success || notice.informational) ? MB_ICONINFORMATION : MB_ICONWARNING)
            );
            InvalidateRect(hwnd, nullptr, FALSE);
            return 0;
        }

        case WM_LOGIN_OPEN_URL: {
            std::vector<std::wstring> urls;
            {
                std::lock_guard<std::mutex> lock(g_app.uiQueueMutex);
                while (!g_app.pendingLoginUrls.empty()) {
                    urls.push_back(g_app.pendingLoginUrls.front());
                    g_app.pendingLoginUrls.pop_front();
                }
            }
            for (const auto& url : urls) {
                std::wstring target = Trim(url);
                if (!target.empty() && IsSafeLoginUrl(target)) {
                    ShellExecuteW(hwnd, L"open", target.c_str(), nullptr, nullptr, SW_SHOWNORMAL);
                } else if (!target.empty()) {
                    MessageBoxW(hwnd, L"Codex returned a login URL with an unsupported scheme.", L"Codex login", MB_OK | MB_ICONWARNING);
                }
            }
            return 0;
        }

        case WM_DESTROY:
            g_app.shuttingDown = true;
            KillTimer(hwnd, TIMER_REFRESH);
            KillTimer(hwnd, TIMER_TASKBAR_REPOSITION);
            KillTimer(hwnd, TIMER_FLYOUT_WATCHDOG);
            DestroyTaskbarPresence();
            if (g_app.codexBarFlyoutWindow && IsWindow(g_app.codexBarFlyoutWindow)) {
                DestroyWindow(g_app.codexBarFlyoutWindow);
                g_app.codexBarFlyoutWindow = nullptr;
            }
            if (g_app.contextMenuWindow && IsWindow(g_app.contextMenuWindow)) {
                DestroyWindow(g_app.contextMenuWindow);
                g_app.contextMenuWindow = nullptr;
            }
            if (g_app.settingsWindow && IsWindow(g_app.settingsWindow)) {
                DestroyWindow(g_app.settingsWindow);
                g_app.settingsWindow = nullptr;
            }
            if (g_app.settingsFont) {
                DeleteObject(g_app.settingsFont);
                g_app.settingsFont = nullptr;
            }
            if (g_app.settingsTitleFont) {
                DeleteObject(g_app.settingsTitleFont);
                g_app.settingsTitleFont = nullptr;
            }
            g_app.settingsFontDpi = 0;
            RemoveTrayIcon();
            if (g_app.refreshThread.joinable()) {
                g_app.refreshThread.join();
            }
            if (g_app.loginThread.joinable()) {
                g_app.loginThread.join();
            }
            PostQuitMessage(0);
            if (g_singleInstanceMutex) {
                CloseHandle(g_singleInstanceMutex);
                g_singleInstanceMutex = nullptr;
            }
            return 0;

        default:
            return DefWindowProcW(hwnd, msg, wParam, lParam);
    }
}

bool RegisterClasses(HINSTANCE instance) {
    LoadAppIconsForDpi(GetDpiForHwnd(nullptr));

    WNDCLASSEXW mainClass{};
    mainClass.cbSize = sizeof(mainClass);
    mainClass.lpfnWndProc = MainProc;
    mainClass.hInstance = instance;
    mainClass.lpszClassName = kMainClass;
    mainClass.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    mainClass.hIcon = g_app.icon ? g_app.icon : LoadIconW(nullptr, IDI_APPLICATION);
    mainClass.hIconSm = g_app.smallIcon ? g_app.smallIcon : mainClass.hIcon;
    if (!RegisterClassExW(&mainClass)) return false;

    WNDCLASSEXW presenceClass{};
    presenceClass.cbSize = sizeof(presenceClass);
    presenceClass.lpfnWndProc = TaskbarPresenceProc;
    presenceClass.hInstance = instance;
    presenceClass.lpszClassName = kTaskbarPresenceClass;
    presenceClass.hCursor = LoadCursorW(nullptr, IDC_HAND);
    presenceClass.hIcon = g_app.icon ? g_app.icon : LoadIconW(nullptr, IDI_APPLICATION);
    presenceClass.hIconSm = g_app.smallIcon ? g_app.smallIcon : presenceClass.hIcon;
    if (!RegisterClassExW(&presenceClass)) return false;

    WNDCLASSEXW flyoutClass{};
    flyoutClass.cbSize = sizeof(flyoutClass);
    flyoutClass.style = CS_DROPSHADOW;
    flyoutClass.lpfnWndProc = CodexBarFlyoutProc;
    flyoutClass.hInstance = instance;
    flyoutClass.lpszClassName = kCodexBarFlyoutClass;
    flyoutClass.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    flyoutClass.hIcon = g_app.icon ? g_app.icon : LoadIconW(nullptr, IDI_APPLICATION);
    flyoutClass.hIconSm = g_app.smallIcon ? g_app.smallIcon : flyoutClass.hIcon;
    if (!RegisterClassExW(&flyoutClass)) return false;

    WNDCLASSEXW contextMenuClass{};
    contextMenuClass.cbSize = sizeof(contextMenuClass);
    contextMenuClass.style = CS_DROPSHADOW;
    contextMenuClass.lpfnWndProc = ContextMenuProc;
    contextMenuClass.hInstance = instance;
    contextMenuClass.lpszClassName = kContextMenuClass;
    contextMenuClass.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    contextMenuClass.hIcon = g_app.icon ? g_app.icon : LoadIconW(nullptr, IDI_APPLICATION);
    contextMenuClass.hIconSm = g_app.smallIcon ? g_app.smallIcon : contextMenuClass.hIcon;
    contextMenuClass.hbrBackground = nullptr;
    if (!RegisterClassExW(&contextMenuClass)) return false;

    WNDCLASSEXW settingsClass{};
    settingsClass.cbSize = sizeof(settingsClass);
    settingsClass.lpfnWndProc = SettingsProc;
    settingsClass.hInstance = instance;
    settingsClass.lpszClassName = kSettingsClass;
    settingsClass.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    settingsClass.hIcon = g_app.icon ? g_app.icon : LoadIconW(nullptr, IDI_APPLICATION);
    settingsClass.hIconSm = g_app.smallIcon ? g_app.smallIcon : settingsClass.hIcon;
    settingsClass.hbrBackground = nullptr;
    if (!RegisterClassExW(&settingsClass)) return false;

    return true;
}

} // namespace

#ifndef CODEXBAR_PARSER_TESTS
int APIENTRY wWinMain(HINSTANCE hInstance, HINSTANCE, LPWSTR, int) {
    EnableDpiAwareness();
    g_taskbarCreatedMessage = RegisterWindowMessageW(L"TaskbarCreated");
    g_app.instance = hInstance;
    EnsureDirectory(ConfigDir());

    g_singleInstanceMutex = CreateMutexW(nullptr, TRUE, L"CodexSWBarWindows.SingleInstance");
    if (g_singleInstanceMutex && GetLastError() == ERROR_ALREADY_EXISTS) {
        HWND existing = FindWindowW(kMainClass, kAppTitle);
        if (existing) {
            PostMessageW(existing, WM_SHOW_SETTINGS, 0, 0);
        }
        CloseHandle(g_singleInstanceMutex);
        g_singleInstanceMutex = nullptr;
        return 0;
    }

    if (!RegisterClasses(hInstance)) {
        MessageBoxW(nullptr, L"Could not register window classes.", kAppTitle, MB_ICONERROR);
        if (g_singleInstanceMutex) {
            CloseHandle(g_singleInstanceMutex);
            g_singleInstanceMutex = nullptr;
        }
        return 1;
    }

    RECT workArea{};
    SystemParametersInfoW(SPI_GETWORKAREA, 0, &workArea, 0);
    UINT startupDpi = GetDpiForHwnd(nullptr);
    UiScale S{startupDpi};
    int availableWidth = std::max(1, static_cast<int>(workArea.right - workArea.left - 24));
    int availableHeight = std::max(1, static_cast<int>(workArea.bottom - workArea.top - 48));
    auto fitWindowSize = [](int available, int preferred, int comfortableMinimum) {
        int screenMinimum = std::min(comfortableMinimum, available);
        return std::max(screenMinimum, std::min(preferred, available));
    };
    int windowWidth = fitWindowSize(availableWidth, S(1120), S(900));
    int windowHeight = fitWindowSize(availableHeight, S(720), S(620));
    int windowX = workArea.left;
    int windowY = workArea.top + S(24);

    HWND hwnd = CreateWindowExW(
        0,
        kMainClass,
        kAppTitle,
        WS_OVERLAPPEDWINDOW,
        windowX,
        windowY,
        windowWidth,
        windowHeight,
        nullptr,
        nullptr,
        hInstance,
        nullptr
    );
    if (!hwnd) {
        MessageBoxW(nullptr, L"Could not create main window.", kAppTitle, MB_ICONERROR);
        return 1;
    }
    g_app.mainWindow = hwnd;
    ShowWindow(hwnd, SW_HIDE);

    MSG msg{};
    while (GetMessageW(&msg, nullptr, 0, 0)) {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
    return static_cast<int>(msg.wParam);
}
#endif
