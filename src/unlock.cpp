// unlock.cpp - 解锁电脑（手机指纹 + 蓝牙）控制台页面
// TC-tools v0.2.0-rc2
//
// 设计：电脑端的蓝牙 GATT 服务、加密握手、键盘注入由独立组件
//       tctool-unlock.exe（.NET 8，见 tcyunlock/）实现；
//       本文件负责菜单、调用组件、解析其单行 JSON 输出并展示。
#include "unlock.hpp"
#include "util.hpp"

#include <cstdlib>
#include <string>
#include <vector>
#include <windows.h>

using namespace tcu;

namespace unlock {

const char* kExeName = "tctool-unlock.exe";

// ---------------------------------------------------------------- 组件查找 --

static bool fileExistsW(const std::wstring& p) {
  DWORD a = GetFileAttributesW(p.c_str());
  return a != INVALID_FILE_ATTRIBUTES && !(a & FILE_ATTRIBUTE_DIRECTORY);
}

static std::wstring toW(const std::string& s) { return u8w(s); }

std::string findHelper() {
  std::vector<std::string> cands;

  std::string base = exeDir();
  if (!base.empty()) {
    std::string b = base;
    if (b.back() != '\\' && b.back() != '/') b += "\\";
    cands.push_back(b + "unlock\\" + kExeName);
    cands.push_back(b + kExeName);
    // 开发态：dist\tctool.exe + tcyunlock\dist\tctool-unlock.exe
    cands.push_back(b + "..\\tcyunlock\\dist\\" + kExeName);
    cands.push_back(b + "tcyunlock\\dist\\" + kExeName);
  }
  char buf[MAX_PATH] = {0};
  if (SearchPathA(nullptr, kExeName, nullptr, MAX_PATH, buf, nullptr)) cands.push_back(buf);

  for (size_t i = 0; i < cands.size(); ++i) {
    if (fileExistsW(toW(cands[i]))) return cands[i];
  }
  return "";
}

bool helperAvailable() { return !findHelper().empty(); }

// ---------------------------------------------------------------- 结果捕获 --

// 运行组件并捕获输出（组件自身不弹新窗口）
static CmdResult run(const std::string& helper, const std::string& args, unsigned timeoutMs = 60000) {
  std::string cmdLine = "\"" + helper + "\"";
  if (!args.empty()) cmdLine += " " + args;
  return runCmd(cmdLine, timeoutMs);
}

// 交互式运行：继承本进程控制台，用户可直接输入
static int runInteractive(const std::string& helper, const std::string& args) {
  std::string cmdLine = "\"" + helper + "\"";
  if (!args.empty()) cmdLine += " " + args;
  std::wstring cmdw = u8w(cmdLine);
  std::vector<wchar_t> cmd(cmdw.begin(), cmdw.end());
  cmd.push_back(0);

  STARTUPINFOW si{};
  si.cb = sizeof(si);
  PROCESS_INFORMATION pi{};
  if (!CreateProcessW(nullptr, cmd.data(), nullptr, nullptr, TRUE, 0, nullptr, nullptr, &si, &pi)) {
    println(sf("  [x] CreateProcess failed (%lu)", (unsigned long)GetLastError()), CLR_RED);
    return -1;
  }
  WaitForSingleObject(pi.hProcess, INFINITE);
  DWORD ec = 0;
  GetExitCodeProcess(pi.hProcess, &ec);
  CloseHandle(pi.hThread);
  CloseHandle(pi.hProcess);
  return (int)ec;
}

// ------------------------------------------------------------ JSON 极简解析 --

static std::string jsonStr(const std::string& j, const std::string& key) {
  std::string k = "\"" + key + "\"";
  size_t p = j.find(k);
  if (p == std::string::npos) return "";
  p = j.find(':', p + k.size());
  if (p == std::string::npos) return "";
  ++p;
  while (p < j.size() && (j[p] == ' ' || j[p] == '\t')) ++p;
  if (p < j.size() && j[p] == '"') {
    ++p;
    std::string out;
    while (p < j.size() && j[p] != '"') {
      if (j[p] == '\\' && p + 1 < j.size()) ++p;
      out += j[p++];
    }
    return out;
  }
  size_t e = p;
  while (e < j.size() && j[e] != ',' && j[e] != '}') ++e;
  return trim(j.substr(p, e - p));
}

static bool jsonBool(const std::string& j, const std::string& key) {
  return jsonStr(j, key) == "true";
}

// ---------------------------------------------------------------- 状态展示 --

// 返回 true = 组件可用（无论是否已配对）
static bool showStatus(App& a, bool brief) {
  std::string helper = findHelper();
  if (helper.empty()) {
    println(a.f(LK_PU_EXE_MISSING, unlock::kExeName), CLR_RED);
    println(a.tr(LK_PU_EXE_HINT1), CLR_GRAY);
    println(a.tr(LK_PU_EXE_HINT2), CLR_GRAY);
    return false;
  }
  CmdResult r = run(helper, "status --json", 25000);
  if (!r.ran || r.out.empty()) {
    println(a.tr(LK_PU_SVC_STOPPED), CLR_YELLOW);
    return false;
  }

  bool paired = jsonBool(r.out, "paired");
  bool adv = jsonBool(r.out, "advertising");
  bool pwd = jsonBool(r.out, "passwordSet");
  bool psk = !jsonStr(r.out, "pskValid").empty() ? jsonBool(r.out, "pskValid") : true;
  std::string hostId = jsonStr(r.out, "hostId");
  std::string peerName = jsonStr(r.out, "peerName");
  std::string hostName = jsonStr(r.out, "hostName");

  println(paired ? a.tr(LK_PU_PAIRED) : a.tr(LK_PU_NOT_PAIRED), paired ? CLR_GREEN : CLR_YELLOW);
  println(a.tr(pwd ? LK_PU_PWD_SET : LK_PU_PWD_UNSET), pwd ? CLR_GREEN : CLR_YELLOW);
  println(a.tr(adv ? LK_PU_ADV_ON : LK_PU_ADV_OFF), adv ? CLR_GREEN : CLR_GRAY);
  // 仅当"曾经配对过、但密钥被判定失效"时才提示失效；
  // 从未配对（paired=false）属于正常初始状态，不应报错。
  if (paired && !psk) println(a.tr(LK_PU_PSK_BAD), CLR_RED);

  if (!brief) {
    if (!hostName.empty()) println("  " + a.f(LK_PU_HOST, (hostName + "  [" + hostId + "]").c_str()), CLR_GRAY);
    else if (!hostId.empty()) println("  " + a.f(LK_PU_HOST, hostId.c_str()), CLR_GRAY);
    if (!peerName.empty()) println("  " + a.f(LK_PU_PEER, peerName.c_str()), CLR_GRAY);
  }
  return true;
}

bool quickReady(App& a) {
  std::string helper = findHelper();
  if (helper.empty()) return false;
  CmdResult r = run(helper, "status --json", 15000);
  if (!r.ran || r.out.empty()) return false;
  return jsonBool(r.out, "paired") && jsonBool(r.out, "passwordSet");
}

// ------------------------------------------------------------------ 菜单项 --

static void doPair(App& a) {
  std::string helper = findHelper();
  if (helper.empty()) { showStatus(a, true); return; }
  println("", CLR_DEF);
  println("  " + a.tr(LK_PU_PAIR_HEAD), CLR_CYAN);
  println("  " + a.tr(LK_PU_PAIR_NOTE1), CLR_GRAY);
  println("  " + a.tr(LK_PU_PAIR_NOTE2), CLR_YELLOW);
  println("", CLR_DEF);
  // 已配对时提示重新配对的影响
  CmdResult st = run(helper, "status --json", 20000);
  if (st.ran && jsonBool(st.out, "paired")) {
    print("  重新配对将生成新密钥并使旧手机立即失效，继续？(y/n): ", CLR_YELLOW);
    std::string yn = trim(readLine());
    if (!(yn == "y" || yn == "Y")) { println(a.tr(LK_CANCELED), CLR_GRAY); return; }
  }
  int ec = runInteractive(helper, "pair");
  if (ec == 0) println(a.tr(LK_OK), CLR_GREEN);
  else println(a.f(LK_PU_EXITCODE, ec), CLR_RED);
}

static void doSetPassword(App& a) {
  std::string helper = findHelper();
  if (helper.empty()) { showStatus(a, true); return; }
  println("", CLR_DEF);
  println("  " + a.tr(LK_PU_PWD_HEAD), CLR_CYAN);
  println("  " + a.tr(LK_PU_PWD_NOTE), CLR_GRAY);
  println("", CLR_DEF);
  int ec = runInteractive(helper, "set-password");
  if (ec == 0) println(a.tr(LK_PU_PWD_DONE), CLR_GREEN);
  else println(a.tr(LK_PU_PWD_FAIL), CLR_RED);
}

static void doRunService(App& a) {
  std::string helper = findHelper();
  if (helper.empty()) { showStatus(a, true); return; }
  println("", CLR_DEF);
  println("  " + a.tr(LK_PU_RUN_HEAD), CLR_CYAN);
  println("  " + a.tr(LK_PU_RUN_NOTE), CLR_GRAY);
  if (!quickReady(a)) {
    println("  " + a.tr(LK_PU_NEED_PAIR), CLR_YELLOW);
    println("  " + a.tr(LK_PU_NEED_PWD), CLR_YELLOW);
  }
  println("", CLR_DEF);
  int ec = runInteractive(helper, "run");
  println("", CLR_DEF);
  println(a.f(LK_PU_EXITCODE, ec), ec == 0 ? CLR_GREEN : CLR_YELLOW);
  kbWait();
}

static void doForget(App& a) {
  std::string helper = findHelper();
  if (helper.empty()) { showStatus(a, true); return; }
  println("", CLR_DEF);
  println("  " + a.tr(LK_PU_FORGET_HEAD), CLR_CYAN);
  print("  " + a.tr(LK_PU_FORGET_CONFIRM), CLR_YELLOW);
  std::string yn = trim(readLine());
  if (!(yn == "y" || yn == "Y")) { println(a.tr(LK_CANCELED), CLR_GRAY); return; }
  CmdResult r = run(helper, "forget", 30000);
  if (r.ran && r.code == 0) println(a.tr(LK_PU_FORGET_DONE), CLR_GREEN);
  else println(a.f(LK_PU_EXITCODE, r.code), CLR_RED);
}

static void doSelfTest(App& a) {
  std::string helper = findHelper();
  if (helper.empty()) { showStatus(a, true); return; }
  println("", CLR_DEF);
  println("  " + a.tr(LK_PU_SELFTEST_HEAD), CLR_CYAN);
  println("", CLR_DEF);
  CmdResult r = run(helper, "selftest", 60000);
  println("  " + a.tr(LK_PU_CMDOUT), CLR_GRAY);
  if (!r.out.empty()) println(r.out, CLR_DEF);
  println("", CLR_DEF);
  println(a.f(LK_PU_EXITCODE, r.code), r.code == 0 ? CLR_GREEN : CLR_RED);
}

// ------------------------------------------------------------------ 主页面 --
} // namespace unlock

using namespace unlock;   // 主页面复用命名空间内的状态/动作实现

// 全局符号：与 app.hpp 的声明一致，首页直接调用
void pageUnlock(App& a) {
  for (;;) {
    println(" " + std::string(60, '='), CLR_CYAN);
    println("  " + a.tr(LK_PU_TITLE), CLR_CYAN);
    println("", CLR_DEF);
    println("  " + a.tr(LK_PU_DESC1), CLR_GRAY);
    println("  " + a.tr(LK_PU_DESC2), CLR_GRAY);
    println("  " + a.tr(LK_PU_DESC3), CLR_GRAY);
    println("", CLR_DEF);
    println("  " + a.tr(LK_PU_STATUS), CLR_WHITE);
    bool canUse = showStatus(a, true);
    println("", CLR_DEF);
    println(sf("  %d. %s", 1, a.tr(LK_PU_1).c_str()), CLR_DEF);
    println(sf("  %d. %s", 2, a.tr(LK_PU_2).c_str()), CLR_DEF);
    println(sf("  %d. %s", 3, a.tr(LK_PU_3).c_str()), CLR_DEF);
    println(sf("  %d. %s", 4, a.tr(LK_PU_4).c_str()), CLR_DEF);
    println(sf("  %d. %s", 5, a.tr(LK_PU_5).c_str()), CLR_DEF);
    println(sf("  %d. %s", 6, a.tr(LK_PU_6).c_str()), CLR_DEF);
    println(sf("  %d. %s", 7, a.tr(LK_PU_BACK).c_str()), CLR_DEF);
    println("", CLR_DEF);
    int n = pageChoice(1, 7, a);
    if (n == 7) break;
    switch (n) {
      case 1: doPair(a); break;
      case 2: doSetPassword(a); break;
      case 3: showStatus(a, false); println("", CLR_DEF); kbWait(); break;
      case 4: doRunService(a); break;
      case 5: doForget(a); break;
      case 6: doSelfTest(a); kbWait(); break;
    }
    if (!canUse) { println("  " + a.tr(LK_PU_PHONE_NOTE), CLR_GRAY); }
  }
}
