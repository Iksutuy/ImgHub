#!/bin/bash
# imgagent Kivy Android 打包脚本
set -e

echo "=== 检查环境 ==="
command -v buildozer >/dev/null 2>&1 || { echo "安装 buildozer..."; pip install buildozer cython; }
command -v git >/dev/null 2>&1 || { echo "需要 git"; exit 1; }

echo "=== 初始化 buildozer ==="
buildozer init 2>/dev/null || true

echo "=== 修改配置 ==="
cat > buildozer.spec <<'SPEC'
[app]
title = imgagent
package.name = imgagent
package.domain = org.yutsuki
source.dir = .
source.include_exts = py,png,jpg,kv,atlas,json,md
version = 1.0.0
requirements = python3,kivy>=2.3.0,pillow
android.api = 33
android.minapi = 21
android.sdk = 33
android.ndk = 25b
p4a.bootstrap = sdl2
orientation = portrait
fullscreen = 0
spec.passes = []
package.complete = false
user = yutsuki
user.initial_dir = ~/imgagent
SPEC

echo "=== 开始打包（这可能需要几分钟）==="
buildozer android debug

echo "=== 完成 ==="
echo "APK 位置: bin/imgagent-1.0.0-arm64-v8a-debug.apk"
