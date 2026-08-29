#import <Cocoa/Cocoa.h>

static NSString *const AppTitle = @"Русификатор Invokers";

@interface AppDelegate : NSObject <NSApplicationDelegate>
@property(nonatomic, strong) NSWindow *window;
@property(nonatomic, strong) NSTextField *badge;
@property(nonatomic, strong) NSTextField *statusTitle;
@property(nonatomic, strong) NSTextField *statusBody;
@property(nonatomic, strong) NSTextField *clientLine;
@property(nonatomic, strong) NSTextField *pathLine;
@property(nonatomic, strong) NSTextField *activityLine;
@property(nonatomic, strong) NSButton *installButton;
@property(nonatomic, strong) NSButton *restoreButton;
@property(nonatomic, strong) NSButton *refreshButton;
@property(nonatomic, strong) NSProgressIndicator *spinner;
@property(nonatomic, strong) NSTask *task;
@property(nonatomic, copy) NSString *resourcesPath;
@end

@implementation AppDelegate

- (NSTextField *)label:(NSString *)text frame:(NSRect)frame size:(CGFloat)size color:(NSColor *)color {
    NSTextField *label = [NSTextField labelWithString:text ?: @""];
    label.frame = frame;
    label.font = [NSFont systemFontOfSize:size];
    label.textColor = color;
    label.lineBreakMode = NSLineBreakByWordWrapping;
    label.maximumNumberOfLines = 0;
    label.selectable = NO;
    return label;
}

- (NSButton *)button:(NSString *)title action:(SEL)action frame:(NSRect)frame primary:(BOOL)primary {
    NSButton *button = [NSButton buttonWithTitle:title target:self action:action];
    button.frame = frame;
    button.bezelStyle = primary ? NSBezelStyleTexturedRounded : NSBezelStyleRounded;
    button.font = [NSFont systemFontOfSize:14 weight:primary ? NSFontWeightSemibold : NSFontWeightRegular];
    button.keyEquivalent = primary ? @"\r" : @"";
    return button;
}

- (void)applicationDidFinishLaunching:(NSNotification *)notification {
    (void)notification;
    self.resourcesPath = NSBundle.mainBundle.resourcePath;

    NSRect frame = NSMakeRect(0, 0, 720, 530);
    self.window = [[NSWindow alloc] initWithContentRect:frame
                                              styleMask:(NSWindowStyleMaskTitled |
                                                         NSWindowStyleMaskClosable |
                                                         NSWindowStyleMaskMiniaturizable)
                                                backing:NSBackingStoreBuffered
                                                  defer:NO];
    self.window.title = AppTitle;
    self.window.releasedWhenClosed = NO;
    [self.window center];

    NSView *content = self.window.contentView;
    content.wantsLayer = YES;
    content.layer.backgroundColor = [NSColor colorWithRed:0.07 green:0.08 blue:0.11 alpha:1].CGColor;

    NSTextField *heading = [self label:AppTitle
                                 frame:NSMakeRect(34, 463, 500, 36)
                                  size:27
                                 color:[NSColor colorWithRed:0.91 green:0.78 blue:0.46 alpha:1]];
    heading.font = [NSFont systemFontOfSize:27 weight:NSFontWeightBold];
    [content addSubview:heading];

    NSTextField *subtitle = [self label:@"Русский интерфейс для нативного клиента macOS"
                                  frame:NSMakeRect(36, 437, 560, 22)
                                   size:13
                                  color:[NSColor colorWithWhite:0.65 alpha:1]];
    [content addSubview:subtitle];

    NSBox *card = [[NSBox alloc] initWithFrame:NSMakeRect(32, 251, 656, 170)];
    card.boxType = NSBoxCustom;
    card.cornerRadius = 12;
    card.fillColor = [NSColor colorWithRed:0.11 green:0.13 blue:0.18 alpha:1];
    card.borderColor = [NSColor colorWithRed:0.25 green:0.30 blue:0.39 alpha:1];
    card.borderWidth = 1;
    [content addSubview:card];

    self.badge = [self label:@"ПРОВЕРКА"
                          frame:NSMakeRect(18, 130, 170, 20)
                           size:11
                          color:[NSColor colorWithRed:0.91 green:0.78 blue:0.46 alpha:1]];
    self.badge.font = [NSFont systemFontOfSize:11 weight:NSFontWeightBold];
    [card.contentView addSubview:self.badge];

    self.statusTitle = [self label:@"Проверяю клиент…"
                                frame:NSMakeRect(18, 95, 610, 30)
                                 size:20
                                color:NSColor.whiteColor];
    self.statusTitle.font = [NSFont systemFontOfSize:20 weight:NSFontWeightSemibold];
    [card.contentView addSubview:self.statusTitle];

    self.statusBody = [self label:@"Секунду."
                               frame:NSMakeRect(18, 49, 610, 44)
                                size:13
                               color:[NSColor colorWithWhite:0.80 alpha:1]];
    [card.contentView addSubview:self.statusBody];

    self.clientLine = [self label:@"Клиент: —"
                               frame:NSMakeRect(18, 25, 610, 19)
                                size:12
                               color:[NSColor colorWithWhite:0.63 alpha:1]];
    [card.contentView addSubview:self.clientLine];

    self.pathLine = [self label:@"Каталог: —"
                             frame:NSMakeRect(18, 7, 610, 18)
                              size:11
                             color:[NSColor colorWithWhite:0.48 alpha:1]];
    self.pathLine.lineBreakMode = NSLineBreakByTruncatingMiddle;
    self.pathLine.maximumNumberOfLines = 1;
    self.pathLine.toolTip = @"";
    [card.contentView addSubview:self.pathLine];

    self.installButton = [self button:@"Установить перевод"
                                  action:@selector(install:)
                                   frame:NSMakeRect(32, 186, 250, 46)
                                 primary:YES];
    [content addSubview:self.installButton];

    self.restoreButton = [self button:@"Восстановить оригинал"
                                  action:@selector(restore:)
                                   frame:NSMakeRect(294, 186, 190, 46)
                                 primary:NO];
    [content addSubview:self.restoreButton];

    self.refreshButton = [self button:@"Проверить"
                                  action:@selector(refresh:)
                                   frame:NSMakeRect(496, 186, 92, 46)
                                 primary:NO];
    [content addSubview:self.refreshButton];

    self.spinner = [[NSProgressIndicator alloc] initWithFrame:NSMakeRect(621, 197, 22, 22)];
    self.spinner.style = NSProgressIndicatorStyleSpinning;
    self.spinner.displayedWhenStopped = NO;
    [content addSubview:self.spinner];

    NSBox *separator = [[NSBox alloc] initWithFrame:NSMakeRect(32, 164, 656, 1)];
    separator.boxType = NSBoxSeparator;
    [content addSubview:separator];

    NSButton *gameButton = [self button:@"Открыть игру"
                                    action:@selector(openGame:)
                                     frame:NSMakeRect(32, 102, 180, 42)
                                   primary:NO];
    [content addSubview:gameButton];

    NSButton *logButton = [self button:@"Показать журнал"
                                   action:@selector(openLog:)
                                    frame:NSMakeRect(224, 102, 180, 42)
                                  primary:NO];
    [content addSubview:logButton];

    NSTextField *platforms = [self label:@"Поддержка: Windows PC и macOS. Android и iOS — позже."
                                      frame:NSMakeRect(420, 111, 250, 30)
                                       size:11
                                      color:[NSColor colorWithWhite:0.55 alpha:1]];
    [content addSubview:platforms];

    NSTextField *warning = [self label:@"Важно: в игре должен оставаться выбран украинский язык. "
                                      "Повторный выбор языка скачивает оригинальный файл и стирает перевод."
                                   frame:NSMakeRect(35, 50, 620, 40)
                                    size:12
                                   color:[NSColor colorWithRed:1.0 green:0.81 blue:0.58 alpha:1]];
    [content addSubview:warning];

    self.activityLine = [self label:@""
                                  frame:NSMakeRect(35, 20, 620, 22)
                                   size:11
                                  color:[NSColor colorWithWhite:0.48 alpha:1]];
    [content addSubview:self.activityLine];

    [self.window makeKeyAndOrderFront:nil];
    [NSApp activateIgnoringOtherApps:YES];
    [self refresh:nil];
}

- (BOOL)applicationShouldTerminateAfterLastWindowClosed:(NSApplication *)sender {
    (void)sender;
    return YES;
}

- (NSString *)scriptVersionAtPath:(NSString *)path {
    NSError *error = nil;
    NSString *source = [NSString stringWithContentsOfFile:path encoding:NSUTF8StringEncoding error:&error];
    if (!source || error) return nil;
    NSRegularExpression *expression = [NSRegularExpression regularExpressionWithPattern:@"(?m)^APP_VERSION=\"([^\"]+)\"$"
                                                                                  options:0
                                                                                    error:nil];
    NSTextCheckingResult *match = [expression firstMatchInString:source options:0 range:NSMakeRange(0, source.length)];
    if (!match || match.numberOfRanges < 2) return nil;
    return [source substringWithRange:[match rangeAtIndex:1]];
}

- (NSString *)patcherPath {
    NSString *bundled = [self.resourcesPath stringByAppendingPathComponent:@"patcher.sh"];
    NSString *home = NSHomeDirectory();
    NSString *updated = [home stringByAppendingPathComponent:@"Library/Application Support/InvokersRu/runtime/patcher.sh"];
    if (![NSFileManager.defaultManager isReadableFileAtPath:updated]) return bundled;

    NSString *bundledVersion = [self scriptVersionAtPath:bundled];
    NSString *updatedVersion = [self scriptVersionAtPath:updated];
    if (!bundledVersion || !updatedVersion) return bundled;
    if ([updatedVersion compare:bundledVersion options:NSNumericSearch] != NSOrderedAscending) return updated;
    return bundled;
}

- (void)setWorking:(BOOL)working message:(NSString *)message {
    self.installButton.enabled = !working;
    self.restoreButton.enabled = !working;
    self.refreshButton.enabled = !working;
    self.activityLine.stringValue = message ?: @"";
    if (working) [self.spinner startAnimation:nil]; else [self.spinner stopAnimation:nil];
}

- (void)runArgument:(NSString *)argument completion:(void (^)(NSString *, int))completion {
    if (self.task.running) return;
    NSTask *task = [[NSTask alloc] init];
    task.executableURL = [NSURL fileURLWithPath:@"/bin/bash"];
    task.arguments = @[[self patcherPath], argument];
    NSMutableDictionary *environment = [NSProcessInfo.processInfo.environment mutableCopy];
    environment[@"INVOKERSRU_RESOURCES"] = self.resourcesPath;
    environment[@"INVOKERSRU_GUI"] = @"1";
    task.environment = environment;
    NSPipe *pipe = [NSPipe pipe];
    task.standardOutput = pipe;
    task.standardError = pipe;
    self.task = task;
    task.terminationHandler = ^(NSTask *finished) {
        NSData *data = [pipe.fileHandleForReading readDataToEndOfFile];
        NSString *output = [[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding] ?: @"";
        dispatch_async(dispatch_get_main_queue(), ^{
            self.task = nil;
            if (completion) completion(output, finished.terminationStatus);
        });
    };
    NSError *error = nil;
    if (![task launchAndReturnError:&error]) {
        self.task = nil;
        if (completion) completion(error.localizedDescription ?: @"Не удалось запустить обработчик.", 127);
    }
}

- (NSDictionary<NSString *, NSString *> *)parseStatus:(NSString *)output {
    NSMutableDictionary *status = [NSMutableDictionary dictionary];
    for (NSString *line in [output componentsSeparatedByCharactersInSet:NSCharacterSet.newlineCharacterSet]) {
        NSRange equal = [line rangeOfString:@"="];
        if (equal.location == NSNotFound) continue;
        NSString *key = [line substringToIndex:equal.location];
        NSString *value = [line substringFromIndex:equal.location + 1];
        if (key.length) status[key] = value ?: @"";
    }
    return status;
}

- (void)applyStatus:(NSDictionary<NSString *, NSString *> *)status {
    NSString *state = status[@"STATE"] ?: @"error";
    NSString *title = status[@"TITLE"] ?: @"Не удалось прочитать состояние";
    NSString *detail = status[@"DETAIL"] ?: @"Откройте журнал для подробностей.";
    NSString *client = status[@"CLIENT"] ?: @"—";
    NSString *version = status[@"VERSION"] ?: @"—";
    NSString *cache = status[@"CACHE"] ?: @"—";
    NSString *running = status[@"RUNNING"] ?: @"no";
    NSString *language = status[@"LANGUAGE"] ?: @"не определён";

    self.statusTitle.stringValue = title;
    self.statusBody.stringValue = detail;
    self.clientLine.stringValue = [NSString stringWithFormat:@"Клиент: %@   •   Версия: %@   •   Язык: %@   •   Игра: %@",
                                   client, version, language,
                                   [running isEqualToString:@"yes"] ? @"запущена" : @"закрыта"];
    self.pathLine.stringValue = [@"Каталог: " stringByAppendingString:cache];
    self.pathLine.toolTip = cache;

    NSColor *accent = [NSColor colorWithRed:0.91 green:0.78 blue:0.46 alpha:1];
    NSString *badge = @"ГОТОВО";
    if ([state isEqualToString:@"russian"]) {
        accent = [NSColor colorWithRed:0.37 green:0.78 blue:0.48 alpha:1];
        badge = @"УСТАНОВЛЕНО";
    } else if ([state isEqualToString:@"missing"] || [state isEqualToString:@"changed"]) {
        accent = [NSColor colorWithRed:0.91 green:0.34 blue:0.30 alpha:1];
        badge = @"НУЖНО ВНИМАНИЕ";
    } else if ([state isEqualToString:@"needs-language"]) {
        accent = [NSColor colorWithRed:0.96 green:0.63 blue:0.24 alpha:1];
        badge = @"НУЖЕН ЯЗЫК";
    }
    self.badge.stringValue = badge;
    self.badge.textColor = accent;
    self.installButton.enabled = [status[@"CAN_INSTALL"] isEqualToString:@"yes"];
    self.restoreButton.enabled = [status[@"CAN_RESTORE"] isEqualToString:@"yes"];
}

- (void)refresh:(id)sender {
    (void)sender;
    [self setWorking:YES message:@"Проверяю активный клиент и языковые файлы…"];
    [self runArgument:@"--gui-status" completion:^(NSString *output, int code) {
        [self setWorking:NO message:code == 0 ? @"Состояние проверено." : @"Проверка завершилась с ошибкой."];
        [self applyStatus:[self parseStatus:output]];
    }];
}

- (void)runAction:(NSString *)argument message:(NSString *)message {
    [self setWorking:YES message:message];
    [self runArgument:argument completion:^(NSString *output, int code) {
        (void)output;
        [self setWorking:NO message:code == 0 ? @"Операция завершена, проверяю результат…" : @"Операция не выполнена."];
        [self refresh:nil];
    }];
}

- (void)install:(id)sender { (void)sender; [self runAction:@"--gui-install" message:@"Собираю и устанавливаю перевод…"]; }
- (void)restore:(id)sender { (void)sender; [self runAction:@"--gui-restore" message:@"Восстанавливаю оригинальный файл…"]; }

- (void)openGame:(id)sender {
    (void)sender;
    NSString *nativeGame = [NSHomeDirectory() stringByAppendingPathComponent:@"Library/Application Support/zone.hitzone.invokers.launcher/game/Invokers.app"];
    NSTask *task = [[NSTask alloc] init];
    task.executableURL = [NSURL fileURLWithPath:@"/usr/bin/open"];
    if ([NSFileManager.defaultManager fileExistsAtPath:nativeGame]) {
        task.arguments = @[@"-n", nativeGame, @"--args", @"-language", @"uk_UA"];
    } else {
        task.arguments = @[@"-a", @"Invokers Titan Legacy"];
    }
    [task launchAndReturnError:nil];
}

- (void)openLog:(id)sender {
    (void)sender;
    NSString *log = [NSHomeDirectory() stringByAppendingPathComponent:@"Library/Application Support/InvokersRu/patcher.log"];
    [NSWorkspace.sharedWorkspace selectFile:log inFileViewerRootedAtPath:log.stringByDeletingLastPathComponent];
}

@end

int main(int argc, const char *argv[]) {
    (void)argc; (void)argv;
    @autoreleasepool {
        NSApplication *app = NSApplication.sharedApplication;
        app.activationPolicy = NSApplicationActivationPolicyRegular;
        AppDelegate *delegate = [[AppDelegate alloc] init];
        app.delegate = delegate;
        [app run];
    }
    return 0;
}
