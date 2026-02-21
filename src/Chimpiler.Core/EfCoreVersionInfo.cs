using Microsoft.EntityFrameworkCore;

namespace Chimpiler.Core;

public static class EfCoreVersionInfo
{
    public static Version RuntimeVersion => typeof(DbContext).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);

    public static int RuntimeMajor => RuntimeVersion.Major;
}
