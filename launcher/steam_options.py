"""Install or remove CAIRN's Steam launch wrapper in a user's localconfig.vdf."""
import argparse
import os
from pathlib import Path
import re
import subprocess
import tempfile
import winreg


APP = ("userlocalconfigstore", "software", "valve", "steam", "apps", "1588550")
TOKEN = re.compile(rb'//[^\r\n]*|"(?:\\.|[^"\\])*"|[{}]|[^\s{}"]+')


def quote(text):
    return b'"' + text.replace(b"\\", b"\\\\").replace(b'"', b'\\"') + b'"'


def unquote(token):
    if not token.startswith(b'"'):
        return token
    return re.sub(rb'\\(["\\])', rb"\1", token[1:-1])


def app_entry(data):
    """Find exact key/value boundaries so Steam's remaining bytes survive intact."""
    body = data[3:] if data.startswith(b"\xef\xbb\xbf") else data
    offset = len(data) - len(body)
    tokens = iter(m for m in TOKEN.finditer(body) if not m[0].startswith(b"//"))
    path = []
    launch, app_end, apps_end = None, None, None
    for key in tokens:
        if key[0] == b"}":
            if not path:
                raise ValueError("Unexpected closing brace in localconfig.vdf")
            if tuple(path) == APP:
                app_end = key.start() + offset
            elif tuple(path) == APP[:-1]:
                apps_end = key.start() + offset
            path.pop()
            continue
        value = next(tokens, None)
        if value is None or value[0] == b"}":
            raise ValueError("Missing VDF value")
        name = unquote(key[0]).decode("utf-8", "replace").lower()
        if value[0] == b"{":
            path.append(name)
        elif tuple(path) == APP and name == "launchoptions":
            if launch is not None:
                raise ValueError("Duplicate CAIRN LaunchOptions")
            launch = (key.start() + offset, value.start() + offset, value.end() + offset)
    if path or apps_end is None:
        raise ValueError("Steam's apps section is missing or incomplete")
    return launch, app_end, apps_end


def updated(data, executable, install):
    launch, app_end, apps_end = app_entry(data)
    old = unquote(data[launch[1]:launch[2]]).decode("utf-8") if launch else ""
    wrapper = '"' + str(executable) + '"'
    if install:
        if wrapper + " %command%" in old:
            return data
        if "%command%" in old:
            value = old.replace("%command%", wrapper + " %command%", 1)
        else:
            value = (wrapper + " %command% " + old).rstrip()
    else:
        if wrapper + " %command%" not in old:
            return data
        value = old.replace(wrapper + " %command%", "%command%", 1)
        if value == "%command%":
            value = ""
        elif value.startswith("%command% "):
            value = value[len("%command% "):]

    newline = b"\r\n" if b"\r\n" in data else b"\n"
    encoded = quote(value.encode("utf-8"))
    if launch:
        key, start, end = launch
        if value:
            return data[:start] + encoded + data[end:]
        first = data.rfind(b"\n", 0, key) + 1
        last = data.find(b"\n", end)
        if not data[first:key].strip() and last >= 0 and not data[end:last].strip():
            return data[:first] + data[last + 1:]
        return data[:key] + data[end:]

    end = app_end if app_end is not None else apps_end
    line_start = data.rfind(b"\n", 0, end) + 1
    indent = data[line_start:end]
    if indent.strip():
        # A compact VDF may place its closing brace on the same line as other keys.
        line_start, indent = end, b""
    field = b'"LaunchOptions"\t\t' + encoded + newline
    if app_end is not None:
        insert = indent + b"\t" + field
    else:
        insert = (indent + b'\t"1588550"' + newline + indent + b"\t{" + newline
                  + indent + b"\t\t" + field + indent + b"\t}" + newline)
    return data[:line_start] + insert + data[line_start:]


def live_steam_file(config):
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam") as key:
            steam = Path(winreg.QueryValueEx(key, "SteamPath")[0]).resolve()
        config.resolve().relative_to(steam / "userdata")
    except (FileNotFoundError, ValueError):
        return False
    processes = subprocess.check_output(["tasklist", "/FI", "IMAGENAME eq steam.exe", "/NH"])
    return b"steam.exe" in processes.lower()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=["install", "remove"])
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--launcher", type=Path, required=True)
    parser.add_argument("--preview", action="store_true", help="Show the resulting setting without writing")
    args = parser.parse_args()
    original = args.config.read_bytes()
    executable = args.launcher.resolve()
    modified = updated(original, executable, args.action == "install")
    launch = app_entry(modified)[0]
    value = unquote(modified[launch[1]:launch[2]]).decode("utf-8") if launch else ""
    print("CAIRN Launch Options =", value)
    if modified == original or args.preview:
        return
    if args.action == "install" and not executable.is_file():
        parser.error("The launcher has not been built or installed at the supplied path.")
    if live_steam_file(args.config):
        parser.error("Exit Steam before editing localconfig.vdf so the running client cannot overwrite it.")
    with tempfile.NamedTemporaryFile(dir=args.config.parent, delete=False) as staged:
        staged.write(modified)
        staged.flush()
        os.fsync(staged.fileno())
    try:
        if args.config.read_bytes() != original:
            raise RuntimeError("Steam's configuration changed during this edit. Retry using its latest version.")
        os.replace(staged.name, args.config)
    finally:
        if os.path.exists(staged.name):
            os.unlink(staged.name)


if __name__ == "__main__":
    main()
