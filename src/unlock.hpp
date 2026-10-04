// unlock.hpp - 手机指纹蓝牙解锁（电脑端 UI 层）
// TC-tools v0.2.0-rc2
//
// 电脑端的蓝牙/加密/解锁逻辑由独立组件 tctool-unlock.exe (.NET 8) 实现，
// 本文件只负责控制台菜单、调用组件、解析 JSON 状态与展示。
#pragma once
#include "app.hpp"
#include <string>

// 解锁子页面（首页第 4 项；实现位于 unlock.cpp，符号为全局名以匹配 app.hpp 声明）
void pageUnlock(App& a);

namespace unlock {

// 菜单输入解析（实现位于 pages.cpp，与首页共用同一套校验与提示）
int pageChoice(int minC, int maxC, App& a);

// 组件可执行文件名
extern const char* kExeName;   // "tctool-unlock.exe"

// 查找组件，找到返回完整路径，找不到返回空串
std::string findHelper();

// 组件是否存在
bool helperAvailable();

// 供外部（首页）使用的状态简述：true = 已配对且密码已设置
bool quickReady(App& a);

} // namespace unlock
