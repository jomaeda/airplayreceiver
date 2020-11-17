﻿using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace AirPlay.Utils
{
    public static class LibraryLoader
    {
        static LibraryLoader()
        {
            if (Environment.OSVersion.Platform == PlatformID.Unix || Environment.OSVersion.Platform == PlatformID.MacOSX || (int)Environment.OSVersion.Platform == 128)
            {
                LoadPosixLibrary();
            }
            else
            {
                // LoadWindowsLibrary();
            }
        }

        static void LoadPosixLibrary()
        {
            const int RTLD_NOW = 2;
            string rootDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            var isOsx = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
            var libFile = isOsx ? "libdl.dylib" : "libdl.so";

            var arch = (isOsx ? "osx" : "linux") + "-" + (Environment.Is64BitProcess ? "x64" : "x86");

            // Search a few different locations for our native assembly
            var paths = new[]
            {
                Path.Combine(rootDirectory, "runtimes", arch, "native", libFile),
                Path.Combine(rootDirectory, libFile),
                Path.Combine("/usr/local/lib", libFile),
                Path.Combine("/usr/lib", libFile)
            };

            foreach (var path in paths)
            {
                if (path == null)
                {
                    continue;
                }

                if (File.Exists(path))
                {
                    var addr = dlopen(path, RTLD_NOW);
                    if (addr == IntPtr.Zero)
                    {
                        var error = Marshal.PtrToStringAnsi(dlerror());
                        throw new Exception("dlopen failed: " + path + " : " + error);
                    }

                    return;
                }
            }

            throw new Exception("dlopen failed: unable to locate library " + libFile + ". Searched: " + paths.Aggregate((a, b) => a + "; " + b));
        }

        [DllImport("libdl")]
        public static extern IntPtr dlopen(string fileName, int flags);

        [DllImport("libdl")]
        public static extern IntPtr dlerror();

        [DllImport("libdl")]
        public static extern IntPtr dlsym(IntPtr handle, string symbol);

        [DllImport("libdl")]
        public static extern IntPtr dlclose(IntPtr handle);
    }
}