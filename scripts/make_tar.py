"""把目录打成 tar.gz，并强制文件权限为 0755。

Windows 自带 tar 会写出 -rw-rw-rw-，Linux 用户解压后没有执行位；
用本脚本打包可以保留可执行权限。用法：python make_tar.py <源目录> <输出.tar.gz>
"""
import os
import sys
import tarfile


def main() -> int:
    if len(sys.argv) != 3:
        print("usage: python make_tar.py <src-dir> <out.tar.gz>", file=sys.stderr)
        return 2
    src, out = sys.argv[1], sys.argv[2]
    with tarfile.open(out, "w:gz") as tar:
        for root, _dirs, files in os.walk(src):
            for name in sorted(files):
                full = os.path.join(root, name)
                arc = os.path.relpath(full, src).replace(os.sep, "/")
                info = tar.gettarinfo(full, arcname=arc)
                info.mode = 0o755
                info.uid = info.gid = 0
                info.uname = info.gname = ""
                with open(full, "rb") as fh:
                    tar.addfile(info, fh)
    return 0


if __name__ == "__main__":
    sys.exit(main())
