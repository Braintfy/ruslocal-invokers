// Bundle entry point for "Русификатор Invokers".
//
// This exists purely so the bundle's main executable is a real Mach-O binary. macOS attributes a TCC
// grant such as Full Disk Access to the responsible process, and when a bundle's main executable is a
// shell script the running process is /bin/bash — a platform binary the grant cannot be pinned to. The
// switch in System Settings then looks enabled while the process still cannot read the game container:
// directory listings succeed and every read fails with EPERM.
//
// Launching the script as a child of this binary makes the bundle itself the responsible process, so
// the grant applies to the script and to every helper it runs. The parent deliberately stays alive and
// waits, rather than exec'ing the interpreter over itself, so responsibility is inherited rather than
// re-evaluated against bash.

#include <errno.h>
#include <libgen.h>
#include <limits.h>
#include <mach-o/dyld.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/wait.h>
#include <unistd.h>

int main(int argc, char *argv[]) {
    char executable[PATH_MAX];
    uint32_t size = (uint32_t)sizeof(executable);
    if (_NSGetExecutablePath(executable, &size) != 0) {
        fprintf(stderr, "launcher: executable path does not fit in PATH_MAX\n");
        return 1;
    }

    char resolved[PATH_MAX];
    if (realpath(executable, resolved) == NULL) {
        fprintf(stderr, "launcher: realpath failed: %s\n", strerror(errno));
        return 1;
    }

    // .../Contents/MacOS/<launcher> -> .../Contents/Resources/patcher.sh
    char *macos_dir = dirname(resolved);
    char contents_copy[PATH_MAX];
    snprintf(contents_copy, sizeof(contents_copy), "%s", macos_dir);
    char *contents_dir = dirname(contents_copy);

    char script[PATH_MAX];
    int written = snprintf(script, sizeof(script), "%s/Resources/patcher.sh", contents_dir);
    if (written < 0 || (size_t)written >= sizeof(script)) {
        fprintf(stderr, "launcher: script path does not fit in PATH_MAX\n");
        return 1;
    }

    if (access(script, R_OK) != 0) {
        fprintf(stderr, "launcher: cannot read %s: %s\n", script, strerror(errno));
        return 1;
    }

    char **child_argv = calloc((size_t)argc + 2, sizeof(char *));
    if (child_argv == NULL) {
        fprintf(stderr, "launcher: out of memory\n");
        return 1;
    }
    child_argv[0] = "/bin/bash";
    child_argv[1] = script;
    for (int i = 1; i < argc; i++) {
        child_argv[i + 1] = argv[i];
    }
    child_argv[argc + 1] = NULL;

    pid_t child = fork();
    if (child < 0) {
        fprintf(stderr, "launcher: fork failed: %s\n", strerror(errno));
        free(child_argv);
        return 1;
    }

    if (child == 0) {
        execv("/bin/bash", child_argv);
        fprintf(stderr, "launcher: execv failed: %s\n", strerror(errno));
        _exit(127);
    }

    free(child_argv);

    int status = 0;
    while (waitpid(child, &status, 0) < 0) {
        if (errno != EINTR) {
            fprintf(stderr, "launcher: waitpid failed: %s\n", strerror(errno));
            return 1;
        }
    }

    if (WIFEXITED(status)) {
        return WEXITSTATUS(status);
    }
    return 1;
}
