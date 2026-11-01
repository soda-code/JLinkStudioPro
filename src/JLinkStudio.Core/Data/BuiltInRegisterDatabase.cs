using JLinkStudio.Core.Models;

namespace JLinkStudio.Core.Data;

/// <summary>
/// 内置寄存器参考表（来源：STM32F4xx 参考手册 RM0090 常用外设章节）。
/// 硬件寄存器页面直接使用本表，不再依赖外部 .svd 文件。
/// 收录的是最常用的外设与寄存器，地址 / 偏移按手册给出。
/// </summary>
public static class BuiltInRegisterDatabase
{
    /// <summary>位域定义：名称 / 起始位 / 位宽 / 说明。</summary>
    private readonly record struct Field(string Name, int Offset, int Width, string Desc);

    private readonly record struct Reg(string Name, ulong Offset, ulong Reset, string Access, string Desc, Field[]? Fields);

    /// <summary>默认寄存器模型（STM32F4xx，只读使用，不要在运行时修改）。</summary>
    public static SvdModel Model { get; } = BuildStm32F4("STM32F4xx");

    private static readonly Dictionary<string, SvdModel> ModelCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 按芯片型号返回对应的内置寄存器表：
    /// STM32F4 / GD32F1xx / GD32F3xx / GD32F4xx / LPC17xx / i.MX RT10xx，其余型号回退到通用 Cortex-M。
    /// </summary>
    public static SvdModel ForDevice(string? device)
    {
        string key = (device ?? string.Empty).Trim().ToUpperInvariant();
        string family = ResolveFamily(key);

        if (ModelCache.TryGetValue(family, out var cached)) return cached;

        var model = family switch
        {
            "GD32F1" => BuildGd32F1("GD32F1xx"),
            "GD32F3" => BuildGd32F1("GD32F3xx"),
            "GD32F4" => BuildStm32F4("GD32F4xx"),
            "LPC17" => BuildLpc17xx("LPC17xx"),
            "IMXRT" => BuildImxrt("i.MX RT10xx"),
            "STM32F1" => BuildStm32F1("STM32F1xx"),
            "STM32F4" => BuildStm32F4("STM32F4xx"),
            _ => BuildGenericCortexM(key.Length == 0 ? "通用 Cortex-M" : key),
        };

        ModelCache[family] = model;
        return model;
    }

    /// <summary>把具体型号归并到寄存器兼容的外设家族。</summary>
    private static string ResolveFamily(string device)
    {
        if (device.StartsWith("GD32F1", StringComparison.Ordinal)) return "GD32F1";
        if (device.StartsWith("GD32F3", StringComparison.Ordinal)) return "GD32F3";
        if (device.StartsWith("GD32F4", StringComparison.Ordinal)) return "GD32F4";
        if (device.StartsWith("LPC17", StringComparison.Ordinal)) return "LPC17";
        if (device.StartsWith("MIMXRT", StringComparison.Ordinal) || device.StartsWith("MK", StringComparison.Ordinal)) return "IMXRT";

        // STM32：F1/F3/L1 是 CRL/CRH 风格，其余（F2/F4/F7/H7/L4/G0/G4…）统一按 MODER 风格近似
        if (device.StartsWith("STM32F1", StringComparison.Ordinal) ||
            device.StartsWith("STM32F3", StringComparison.Ordinal) ||
            device.StartsWith("STM32L1", StringComparison.Ordinal)) return "STM32F1";
        if (device.StartsWith("STM32", StringComparison.Ordinal)) return "STM32F4";

        return "GENERIC";
    }

    private static Reg R(string name, ulong offset, string desc, ulong reset = 0, string access = "read-write",
        Field[]? fields = null)
        => new(name, offset, reset, access, desc, fields);

    private static Field F(string name, int offset, int width, string desc) => new(name, offset, width, desc);

    /// <summary>生成 GPIO 的 16 个按位域（每引脚 width 位）。</summary>
    private static Field[] Pins(string prefix, int width, string desc)
    {
        var fields = new Field[16];
        for (int i = 0; i < 16; i++)
            fields[i] = new Field($"{prefix}{i}", i * width, width, $"{desc}（引脚 {i}）");
        return fields;
    }

    /// <summary>生成 GPIO 复用功能寄存器的 8 个位域（每引脚 4 位）。</summary>
    private static Field[] Afr(string prefix, int startPin)
    {
        var fields = new Field[8];
        for (int i = 0; i < 8; i++)
            fields[i] = new Field($"{prefix}{startPin + i}", i * 4, 4, $"引脚 {startPin + i} 复用功能编号");
        return fields;
    }

    private static SvdModel BuildStm32F4(string modelName)
    {
        var model = new SvdModel
        {
            Name = modelName,
            Version = modelName.StartsWith("GD32", StringComparison.OrdinalIgnoreCase) ? "GD32F4xx 用户手册" : "RM0090",
            Description = $"{modelName} · 内置常用寄存器表",
        };

        Add(model, "SCB", "内核", 0xE000ED00, "系统控制块（HardFault 分析核心）", new[]
        {
            R("CPUID", 0x00, "CPUID 基址寄存器", 0x410FC241, "read-only"),
            R("ICSR", 0x04, "中断控制与状态寄存器：VECTACTIVE / VECTPENDING / RETTOBASE"),
            R("VTOR", 0x08, "向量表偏移寄存器"),
            R("AIRCR", 0x0C, "应用中断与复位控制寄存器：SYSRESETREQ / VECTKEY"),
            R("SCR", 0x10, "系统控制寄存器：SLEEPONEXIT / SLEEPDEEP / SEVONPEND"),
            R("CCR", 0x14, "配置与控制寄存器：STKALIGN / UNALIGN_TRP / DIV_0_TRP"),
            R("SHPR1", 0x18, "系统异常优先级 1（MemManage / BusFault / UsageFault）"),
            R("SHPR2", 0x1C, "系统异常优先级 2（SVCall）"),
            R("SHPR3", 0x20, "系统异常优先级 3（PendSV / SysTick）"),
            R("SHCSR", 0x24, "系统处理控制与状态寄存器"),
            R("CFSR", 0x28, "可配置故障状态寄存器：MMARVALID / BFARVALID / UNDEFINSTR / INVSTATE", 0, "read-write", new[]
            {
                F("IACCVIOL", 0, 1, "取指访问违例"),
                F("DACCVIOL", 1, 1, "数据访问违例"),
                F("MUNSTKERR", 3, 1, "出栈时存储器管理错误"),
                F("MSTKERR", 4, 1, "入栈时存储器管理错误"),
                F("MMARVALID", 7, 1, "MMFAR 中的地址有效"),
                F("IBUSERR", 8, 1, "取指总线错误"),
                F("PRECISERR", 9, 1, "精确数据总线错误（BFAR 有效）"),
                F("IMPRECISERR", 10, 1, "不精确数据总线错误"),
                F("UNSTKERR", 11, 1, "出栈总线错误"),
                F("STKERR", 12, 1, "入栈总线错误"),
                F("BFARVALID", 15, 1, "BFAR 中的地址有效"),
                F("UNDEFINSTR", 16, 1, "未定义指令"),
                F("INVSTATE", 17, 1, "非法状态（Thumb 位为 0）"),
                F("INVPC", 18, 1, "非法 PC 装载"),
                F("NOCP", 19, 1, "协处理器不可用"),
                F("UNALIGNED", 24, 1, "非对齐访问"),
                F("DIVBYZERO", 25, 1, "除零"),
            }),
            R("HFSR", 0x2C, "硬故障状态寄存器：FORCED / VECTTBL / DEBUGEVT", 0, "read-write", new[]
            {
                F("VECTTBL", 1, 1, "向量表读取总线错误"),
                F("FORCED", 30, 1, "由其他故障升级为硬故障"),
                F("DEBUGEVT", 31, 1, "调试事件触发"),
            }),
            R("DFSR", 0x30, "调试故障状态寄存器"),
            R("MMFAR", 0x34, "存储器管理故障地址寄存器"),
            R("BFAR", 0x38, "总线故障地址寄存器"),
            R("AFSR", 0x3C, "辅助故障状态寄存器"),
        });

        var gpio = new[]
        {
            R("MODER", 0x00, "端口模式寄存器：每引脚 2 位（00 输入 / 01 输出 / 10 复用 / 11 模拟）", 0, "read-write",
                Pins("MODER", 2, "00 输入 / 01 输出 / 10 复用 / 11 模拟")),
            R("OTYPER", 0x04, "输出类型寄存器：0 推挽 / 1 开漏", 0, "read-write",
                Pins("OT", 1, "0 推挽 / 1 开漏")),
            R("OSPEEDR", 0x08, "输出速度寄存器：每引脚 2 位", 0, "read-write",
                Pins("OSPEEDR", 2, "00 低速 / 01 中速 / 10 高速 / 11 超高速")),
            R("PUPDR", 0x0C, "上下拉寄存器：00 无 / 01 上拉 / 10 下拉", 0, "read-write",
                Pins("PUPDR", 2, "00 无 / 01 上拉 / 10 下拉")),
            R("IDR", 0x10, "输入数据寄存器", 0, "read-only",
                Pins("IDR", 1, "输入电平")),
            R("ODR", 0x14, "输出数据寄存器", 0, "read-write",
                Pins("ODR", 1, "输出电平")),
            R("BSRR", 0x18, "位设置 / 复位寄存器：低 16 位置位，高 16 位复位", 0, "write-only", new[]
            {
                F("BS0-15", 0, 16, "置位对应引脚（写 1 有效）"),
                F("BR0-15", 16, 16, "复位对应引脚（写 1 有效）"),
            }),
            R("LCKR", 0x1C, "配置锁定寄存器", 0, "read-write", new[]
            {
                F("LC0-15", 0, 16, "引脚配置锁定"),
                F("LCKK", 16, 1, "锁定键（需按序列写入）"),
            }),
            R("AFR0", 0x20, "复用功能低寄存器（引脚 0-7）", 0, "read-write", Afr("AFRL", 0)),
            R("AFR1", 0x24, "复用功能高寄存器（引脚 8-15）", 0, "read-write", Afr("AFRH", 8)),
        };

        string[] gpioNames = { "GPIOA", "GPIOB", "GPIOC", "GPIOD", "GPIOE", "GPIOF", "GPIOG", "GPIOH", "GPIOI" };
        for (int i = 0; i < gpioNames.Length; i++)
            Add(model, gpioNames[i], "GPIO", 0x40020000 + (ulong)(0x400 * i), "通用 I/O 端口", gpio);

        Add(model, "RCC", "RCC", 0x40023800, "复位与时钟控制", new[]
        {
            R("CR", 0x00, "时钟控制寄存器：HSION / HSIRDY / PLLON / PLLRDY / CSSON", 0x00000083, "read-write", new[]
            {
                F("HSION", 0, 1, "内部高速时钟使能"),
                F("HSIRDY", 1, 1, "内部高速时钟就绪"),
                F("HSITRIM", 3, 5, "内部高速时钟校准值"),
                F("HSICAL", 8, 8, "内部高速时钟校准（只读）"),
                F("HSEON", 16, 1, "外部高速时钟使能"),
                F("HSERDY", 17, 1, "外部高速时钟就绪"),
                F("HSEBYP", 18, 1, "外部高速时钟旁路"),
                F("CSSON", 19, 1, "时钟安全系统使能"),
                F("PLLON", 24, 1, "主 PLL 使能"),
                F("PLLRDY", 25, 1, "主 PLL 就绪"),
                F("PLLI2SON", 26, 1, "PLLI2S 使能"),
                F("PLLI2SRDY", 27, 1, "PLLI2S 就绪"),
            }),
            R("PLLCFGR", 0x04, "PLL 配置寄存器：PLLM / PLLN / PLLP / PLLQ / PLLSRC", 0x24003010, "read-write", new[]
            {
                F("PLLM", 0, 6, "PLL 输入分频"),
                F("PLLN", 6, 9, "PLL 倍频"),
                F("PLLP", 16, 2, "PLL 系统输出分频：00 /2、01 /4、10 /6、11 /8"),
                F("PLLSRC", 22, 1, "PLL 输入源：0 HSI / 1 HSE"),
                F("PLLQ", 24, 4, "PLL 48MHz 输出分频"),
            }),
            R("CFGR", 0x08, "时钟配置寄存器：SW / SWS / HPRE / PPRE1 / PPRE2", 0, "read-write", new[]
            {
                F("SW", 0, 2, "系统时钟选择：00 HSI / 01 HSE / 10 PLL"),
                F("SWS", 2, 2, "系统时钟切换状态（只读）"),
                F("HPRE", 4, 4, "AHB 预分频"),
                F("PPRE1", 10, 3, "APB1 低速预分频"),
                F("PPRE2", 13, 3, "APB2 高速预分频"),
                F("RTCPRE", 16, 5, "RTC 时钟预分频"),
                F("MCO1", 21, 2, "MCO1 输出选择"),
                F("I2SSRC", 23, 1, "I2S 时钟源"),
                F("MCO1PRE", 24, 3, "MCO1 预分频"),
                F("MCO2PRE", 27, 3, "MCO2 预分频"),
                F("MCO2", 30, 2, "MCO2 输出选择"),
            }),
            R("CIR", 0x0C, "时钟中断寄存器：LSIRDYF / HSERDYF / PLLRDYF 及使能"),
            R("AHB1RSTR", 0x10, "AHB1 外设复位寄存器"),
            R("AHB2RSTR", 0x14, "AHB2 外设复位寄存器"),
            R("AHB3RSTR", 0x18, "AHB3 外设复位寄存器"),
            R("APB1RSTR", 0x20, "APB1 外设复位寄存器"),
            R("APB2RSTR", 0x24, "APB2 外设复位寄存器"),
            R("AHB1ENR", 0x30, "AHB1 外设时钟使能：GPIOAEN / DMA1EN / DMA2EN"),
            R("AHB2ENR", 0x34, "AHB2 外设时钟使能：USBOTGFSEN / RNGEN"),
            R("AHB3ENR", 0x38, "AHB3 外设时钟使能：FSMCEN"),
            R("APB1ENR", 0x40, "APB1 外设时钟使能：TIM2EN / USART2EN / I2C1EN"),
            R("APB2ENR", 0x44, "APB2 外设时钟使能：TIM1EN / USART1EN / ADC1EN"),
            R("AHB1LPENR", 0x50, "AHB1 低功耗时钟使能"),
            R("AHB2LPENR", 0x54, "AHB2 低功耗时钟使能"),
            R("AHB3LPENR", 0x58, "AHB3 低功耗时钟使能"),
            R("APB1LPENR", 0x60, "APB1 低功耗时钟使能"),
            R("APB2LPENR", 0x64, "APB2 低功耗时钟使能"),
            R("BDCR", 0x70, "备份域控制寄存器：LSEON / RTCSEL / BDRST"),
            R("CSR", 0x74, "时钟控制与状态寄存器：LSION / RMVF / BORRSTF"),
            R("SSCGR", 0x80, "扩频时钟生成寄存器"),
            R("PLLI2SCFGR", 0x84, "PLLI2S 配置寄存器"),
        });

        var usart = new[]
        {
            R("SR", 0x00, "状态寄存器：TXE / RXNE / TC / ORE / FE / PE", 0x00C0, "read-only", new[]
            {
                F("PE", 0, 1, "校验错误"),
                F("FE", 1, 1, "帧错误"),
                F("NF", 2, 1, "噪声检测"),
                F("ORE", 3, 1, "溢出错误"),
                F("IDLE", 4, 1, "空闲线路检测"),
                F("RXNE", 5, 1, "读数据寄存器非空"),
                F("TC", 6, 1, "发送完成"),
                F("TXE", 7, 1, "发送数据寄存器空"),
                F("LBD", 8, 1, "LIN 断开检测"),
                F("CTS", 9, 1, "CTS 变化"),
            }),
            R("DR", 0x04, "数据寄存器：发送 / 接收"),
            R("BRR", 0x08, "波特率寄存器"),
            R("CR1", 0x0C, "控制寄存器 1：UE / M / PCE / PS / TE / RE / RXNEIE", 0, "read-write", new[]
            {
                F("SBK", 0, 1, "发送断开字符"),
                F("RWT", 1, 1, "接收唤醒"),
                F("RE", 2, 1, "接收使能"),
                F("TE", 3, 1, "发送使能"),
                F("IDLEIE", 4, 1, "空闲中断使能"),
                F("RXNEIE", 5, 1, "接收非空中断使能"),
                F("TCIE", 6, 1, "发送完成中断使能"),
                F("TXEIE", 7, 1, "发送空中断使能"),
                F("PEIE", 8, 1, "校验错误中断使能"),
                F("PS", 9, 1, "校验位选择：0 偶 / 1 奇"),
                F("PCE", 10, 1, "校验控制使能"),
                F("WAKE", 11, 1, "唤醒方式"),
                F("M", 12, 1, "字长：0 8 位 / 1 9 位"),
                F("UE", 13, 1, "USART 使能"),
                F("OVER8", 15, 1, "过采样模式：0 16 倍 / 1 8 倍"),
            }),
            R("CR2", 0x10, "控制寄存器 2：STOP"),
            R("CR3", 0x14, "控制寄存器 3：EIE / HDSEL / DMAR / DMAT"),
            R("GTPR", 0x18, "保护时间与预分频寄存器"),
        };

        Add(model, "USART1", "USART", 0x40011000, "通用同步异步收发器 1（APB2）", usart);
        Add(model, "USART2", "USART", 0x40004400, "通用同步异步收发器 2（APB1）", usart);
        Add(model, "USART3", "USART", 0x40004800, "通用同步异步收发器 3（APB1）", usart);
        Add(model, "UART4", "USART", 0x40004C00, "通用异步收发器 4（APB1）", usart);
        Add(model, "UART5", "USART", 0x40005000, "通用异步收发器 5（APB1）", usart);
        Add(model, "USART6", "USART", 0x40011400, "通用同步异步收发器 6（APB2）", usart);

        var timGeneral = new[]
        {
            R("CR1", 0x00, "控制寄存器 1：CEN / UDIS / OPM / DIR / CMS / ARPE", 0, "read-write", new[]
            {
                F("CEN", 0, 1, "计数器使能"),
                F("UDIS", 1, 1, "更新禁止"),
                F("URS", 2, 1, "更新请求源"),
                F("OPM", 3, 1, "单脉冲模式"),
                F("DIR", 4, 1, "计数方向：0 向上 / 1 向下"),
                F("CMS", 5, 2, "对齐模式：00 边沿 / 01/10/11 中央对齐"),
                F("ARPE", 7, 1, "自动重装载预装载使能"),
                F("CKD", 8, 2, "时钟分频因子"),
            }),
            R("CR2", 0x04, "控制寄存器 2：CCDS / MMS / TI1S"),
            R("SMCR", 0x08, "从模式控制寄存器：SMS / TS / ECE / ETP"),
            R("DIER", 0x0C, "DMA / 中断使能寄存器：UIE / CC1IE / TIE", 0, "read-write", new[]
            {
                F("UIE", 0, 1, "更新中断使能"),
                F("CC1IE", 1, 1, "捕获/比较 1 中断使能"),
                F("CC2IE", 2, 1, "捕获/比较 2 中断使能"),
                F("CC3IE", 3, 1, "捕获/比较 3 中断使能"),
                F("CC4IE", 4, 1, "捕获/比较 4 中断使能"),
                F("COMIE", 5, 1, "COM 中断使能"),
                F("TIE", 6, 1, "触发中断使能"),
                F("BIE", 7, 1, "刹车中断使能"),
                F("UDE", 8, 1, "更新 DMA 请求使能"),
                F("CC1DE", 9, 1, "捕获/比较 1 DMA 请求使能"),
                F("TDE", 14, 1, "触发 DMA 请求使能"),
            }),
            R("SR", 0x10, "状态寄存器：UIF / CC1IF / TIF / CC1OF", 0, "read-write", new[]
            {
                F("UIF", 0, 1, "更新中断标志"),
                F("CC1IF", 1, 1, "捕获/比较 1 中断标志"),
                F("CC2IF", 2, 1, "捕获/比较 2 中断标志"),
                F("CC3IF", 3, 1, "捕获/比较 3 中断标志"),
                F("CC4IF", 4, 1, "捕获/比较 4 中断标志"),
                F("COMIF", 5, 1, "COM 中断标志"),
                F("TIF", 6, 1, "触发中断标志"),
                F("BIF", 7, 1, "刹车中断标志"),
                F("CC1OF", 9, 1, "捕获/比较 1 溢出"),
                F("CC2OF", 10, 1, "捕获/比较 2 溢出"),
                F("CC3OF", 11, 1, "捕获/比较 3 溢出"),
                F("CC4OF", 12, 1, "捕获/比较 4 溢出"),
            }),
            R("EGR", 0x14, "事件产生寄存器：UG / CC1G / TG"),
            R("CCMR1", 0x18, "捕获/比较模式寄存器 1：OC1M / OC1PE / CC1S"),
            R("CCMR2", 0x1C, "捕获/比较模式寄存器 2：OC3M / OC4M"),
            R("CCER", 0x20, "捕获/比较使能寄存器：CC1E / CC1P"),
            R("CNT", 0x24, "计数器"),
            R("PSC", 0x28, "预分频器"),
            R("ARR", 0x2C, "自动重装载寄存器"),
            R("RCR", 0x30, "重复计数寄存器（仅高级定时器）"),
            R("CCR1", 0x34, "捕获/比较寄存器 1"),
            R("CCR2", 0x38, "捕获/比较寄存器 2"),
            R("CCR3", 0x3C, "捕获/比较寄存器 3"),
            R("CCR4", 0x40, "捕获/比较寄存器 4"),
            R("BDTR", 0x44, "断路与死区寄存器（仅高级定时器）"),
            R("DCR", 0x48, "DMA 控制寄存器"),
            R("DMAR", 0x4C, "DMA 地址寄存器（突发）"),
        };

        Add(model, "TIM1", "TIM", 0x40010000, "高级控制定时器 1（APB2）", timGeneral);
        Add(model, "TIM2", "TIM", 0x40000000, "通用定时器 2（APB1）", timGeneral);
        Add(model, "TIM3", "TIM", 0x40000400, "通用定时器 3（APB1）", timGeneral);
        Add(model, "TIM4", "TIM", 0x40000800, "通用定时器 4（APB1）", timGeneral);
        Add(model, "TIM5", "TIM", 0x40000C00, "通用定时器 5（APB1）", timGeneral);
        Add(model, "TIM6", "TIM", 0x40001000, "基本定时器 6（APB1）", timGeneral);
        Add(model, "TIM7", "TIM", 0x40001400, "基本定时器 7（APB1）", timGeneral);
        Add(model, "TIM8", "TIM", 0x40010400, "高级控制定时器 8（APB2）", timGeneral);
        Add(model, "TIM9", "TIM", 0x40014000, "通用定时器 9（APB2）", timGeneral);
        Add(model, "TIM10", "TIM", 0x40014400, "通用定时器 10（APB2）", timGeneral);
        Add(model, "TIM11", "TIM", 0x40014800, "通用定时器 11（APB2）", timGeneral);
        Add(model, "TIM12", "TIM", 0x40001800, "通用定时器 12（APB1）", timGeneral);
        Add(model, "TIM13", "TIM", 0x40001C00, "通用定时器 13（APB1）", timGeneral);
        Add(model, "TIM14", "TIM", 0x40002000, "通用定时器 14（APB1）", timGeneral);

        var adc = new[]
        {
            R("SR", 0x00, "状态寄存器：AWD / EOC / JEOC / STRT / OVR", 0, "read-write", new[]
            {
                F("AWD", 0, 1, "模拟看门狗标志"),
                F("EOC", 1, 1, "规则转换结束"),
                F("JEOC", 2, 1, "注入转换结束"),
                F("JSTRT", 3, 1, "注入转换已启动"),
                F("STRT", 4, 1, "规则转换已启动"),
                F("OVR", 5, 1, "溢出"),
            }),
            R("CR1", 0x04, "控制寄存器 1：AWDEN / JAWDEN / RES / SCAN / EOCIE", 0, "read-write", new[]
            {
                F("AWDCH", 0, 5, "模拟看门狗通道"),
                F("EOCIE", 5, 1, "规则转换结束中断使能"),
                F("AWDIE", 6, 1, "模拟看门狗中断使能"),
                F("JEOCIE", 7, 1, "注入转换结束中断使能"),
                F("SCAN", 8, 1, "扫描模式"),
                F("AWDSGL", 9, 1, "看门狗单通道使能"),
                F("JAUTO", 10, 1, "自动注入转换"),
                F("DISCEN", 11, 1, "间断模式使能"),
                F("JDISCEN", 12, 1, "注入间断模式使能"),
                F("DISCNUM", 13, 3, "间断通道数"),
                F("JAWDEN", 22, 1, "注入看门狗使能"),
                F("AWDEN", 23, 1, "规则看门狗使能"),
                F("RES", 24, 2, "分辨率：00 12 位 / 01 10 位 / 10 8 位 / 11 6 位"),
                F("OVRIE", 26, 1, "溢出中断使能"),
            }),
            R("CR2", 0x08, "控制寄存器 2：ADON / CONT / DMA / EXTEN / SWSTART", 0, "read-write", new[]
            {
                F("ADON", 0, 1, "ADC 使能"),
                F("CONT", 1, 1, "连续转换"),
                F("DMA", 8, 1, "DMA 使能"),
                F("DDS", 9, 1, "DMA 禁止后继续"),
                F("EOCS", 10, 1, "EOC 选择"),
                F("ALIGN", 11, 1, "数据对齐：0 右 / 1 左"),
                F("JEXTSEL", 16, 4, "注入外部触发选择"),
                F("JEXTEN", 20, 2, "注入外部触发使能与极性"),
                F("JSWSTART", 22, 1, "软件启动注入转换"),
                F("EXTSEL", 24, 4, "规则外部触发选择"),
                F("EXTEN", 28, 2, "规则外部触发使能与极性"),
                F("SWSTART", 30, 1, "软件启动规则转换"),
            }),
            R("SMPR1", 0x0C, "采样时间寄存器 1（通道 10-18）"),
            R("SMPR2", 0x10, "采样时间寄存器 2（通道 0-9）"),
            R("JOFR1", 0x14, "注入通道偏移寄存器 1"),
            R("JOFR2", 0x18, "注入通道偏移寄存器 2"),
            R("JOFR3", 0x1C, "注入通道偏移寄存器 3"),
            R("JOFR4", 0x20, "注入通道偏移寄存器 4"),
            R("HTR", 0x24, "看门狗高阈值寄存器"),
            R("LTR", 0x28, "看门狗低阈值寄存器"),
            R("SQR1", 0x2C, "规则序列寄存器 1：L / SQ16"),
            R("SQR2", 0x30, "规则序列寄存器 2"),
            R("SQR3", 0x34, "规则序列寄存器 3"),
            R("JSQR", 0x38, "注入序列寄存器"),
            R("JDR1", 0x3C, "注入数据寄存器 1", 0, "read-only"),
            R("JDR2", 0x40, "注入数据寄存器 2", 0, "read-only"),
            R("JDR3", 0x44, "注入数据寄存器 3", 0, "read-only"),
            R("JDR4", 0x48, "注入数据寄存器 4", 0, "read-only"),
            R("DR", 0x4C, "规则数据寄存器", 0, "read-only"),
        };

        Add(model, "ADC1", "ADC", 0x40012000, "模数转换器 1", adc);
        Add(model, "ADC2", "ADC", 0x40012100, "模数转换器 2", adc);
        Add(model, "ADC3", "ADC", 0x40012200, "模数转换器 3", adc);

        var spi = new[]
        {
            R("CR1", 0x00, "控制寄存器 1：CPHA / CPOL / MSTR / BR / SPE / LSBFIRST", 0, "read-write", new[]
            {
                F("CPHA", 0, 1, "时钟相位"),
                F("CPOL", 1, 1, "时钟极性"),
                F("MSTR", 2, 1, "主模式选择"),
                F("BR", 3, 3, "波特率预分频"),
                F("SPE", 6, 1, "SPI 使能"),
                F("LSBFIRST", 7, 1, "帧格式：0 MSB 优先 / 1 LSB 优先"),
                F("SSI", 8, 1, "内部从设备选择"),
                F("SSM", 9, 1, "软件从设备管理"),
                F("RXONLY", 10, 1, "只接收模式"),
                F("DFF", 11, 1, "数据帧格式：0 8 位 / 1 16 位"),
                F("CRCNEXT", 12, 1, "下一个发送 CRC"),
                F("CRCEN", 13, 1, "硬件 CRC 使能"),
                F("BIDIOE", 14, 1, "双向模式输出使能"),
                F("BIDIMODE", 15, 1, "双向数据模式使能"),
            }),
            R("CR2", 0x04, "控制寄存器 2：TXEIE / RXNEIE / ERRIE / SSOE"),
            R("SR", 0x08, "状态寄存器：RXNE / TXE / CHSIDE / BSY / OVR / MODF", 0x0002, "read-only", new[]
            {
                F("RXNE", 0, 1, "接收缓冲区非空"),
                F("TXE", 1, 1, "发送缓冲区空"),
                F("CHSIDE", 2, 1, "I2S 声道"),
                F("UDR", 3, 1, "下溢标志"),
                F("CRCERR", 4, 1, "CRC 错误"),
                F("MODF", 5, 1, "模式错误"),
                F("OVR", 6, 1, "溢出"),
                F("BSY", 7, 1, "忙标志"),
                F("FRE", 8, 1, "帧格式错误"),
            }),
            R("DR", 0x0C, "数据寄存器"),
            R("CRCPR", 0x10, "CRC 多项式寄存器"),
            R("RXCRCR", 0x14, "接收 CRC 寄存器", 0, "read-only"),
            R("TXCRCR", 0x18, "发送 CRC 寄存器", 0, "read-only"),
            R("I2SCFGR", 0x1C, "I2S 配置寄存器"),
            R("I2SPR", 0x20, "I2S 预分频寄存器"),
        };

        Add(model, "SPI1", "SPI", 0x40013000, "串行外设接口 1（APB2）", spi);
        Add(model, "SPI2", "SPI", 0x40003800, "串行外设接口 2（APB1）", spi);
        Add(model, "SPI3", "SPI", 0x40003C00, "串行外设接口 3（APB1）", spi);
        Add(model, "SPI4", "SPI", 0x40013400, "串行外设接口 4（APB2）", spi);
        Add(model, "SPI5", "SPI", 0x40015000, "串行外设接口 5（APB2）", spi);
        Add(model, "SPI6", "SPI", 0x40015400, "串行外设接口 6（APB2）", spi);

        var i2c = new[]
        {
            R("CR1", 0x00, "控制寄存器 1：PE / START / STOP / ACK / SWRST"),
            R("CR2", 0x04, "控制寄存器 2：FREQ / ITERREN / ITEVTEN / ITBUFEN"),
            R("OAR1", 0x08, "自身地址寄存器 1"),
            R("OAR2", 0x0C, "自身地址寄存器 2"),
            R("DR", 0x10, "数据寄存器"),
            R("SR1", 0x14, "状态寄存器 1：SB / ADDR / BTF / RXNE / TXE / AF", 0, "read-only", new[]
            {
                F("SB", 0, 1, "起始位已发送"),
                F("ADDR", 1, 1, "地址已发送 / 匹配"),
                F("BTF", 2, 1, "字节传输完成"),
                F("ADD10", 3, 1, "10 位地址头已发送"),
                F("STOPF", 4, 1, "停止位检测"),
                F("RXNE", 6, 1, "数据寄存器非空"),
                F("TXE", 7, 1, "数据寄存器空"),
                F("BERR", 8, 1, "总线错误"),
                F("ARLO", 9, 1, "仲裁丢失"),
                F("AF", 10, 1, "应答失败"),
                F("OVR", 11, 1, "溢出 / 欠载"),
                F("PECERR", 12, 1, "PEC 错误"),
                F("TIMEOUT", 14, 1, "超时"),
                F("SMBALERT", 15, 1, "SMBus 告警"),
            }),
            R("SR2", 0x18, "状态寄存器 2：MSL / BUSY / TRA / GENCALL", 0, "read-only", new[]
            {
                F("MSL", 0, 1, "主 / 从模式"),
                F("BUSY", 1, 1, "总线忙"),
                F("TRA", 2, 1, "发送 / 接收：1 发送"),
                F("GENCALL", 4, 1, "通用调用地址"),
                F("DUALF", 5, 1, "双地址模式"),
                F("SMBHOST", 6, 1, "SMBus 主机头"),
                F("SMBDEFAULT", 7, 1, "SMBus 默认地址"),
                F("PEC", 8, 1, "PEC 字节"),
            }),
            R("CCR", 0x1C, "时钟控制寄存器：FS / DUTY / CCR"),
            R("TRISE", 0x20, "上升时间寄存器"),
            R("FLTR", 0x24, "滤波寄存器"),
        };

        Add(model, "I2C1", "I2C", 0x40005400, "内部集成电路接口 1", i2c);
        Add(model, "I2C2", "I2C", 0x40005800, "内部集成电路接口 2", i2c);
        Add(model, "I2C3", "I2C", 0x40005C00, "内部集成电路接口 3", i2c);

        foreach (var (name, baseAddress, desc) in new[]
                 {
                     ("DMA1", 0x40026000UL, "DMA 控制器 1（8 个 stream）"),
                     ("DMA2", 0x40026400UL, "DMA 控制器 2（8 个 stream）"),
                 })
        {
            var dma = new List<Reg>
            {
                R("LISR", 0x00, "低中断状态寄存器"),
                R("HISR", 0x04, "高中断状态寄存器"),
                R("LIFCR", 0x08, "低中断标志清除寄存器"),
                R("HIFCR", 0x0C, "高中断标志清除寄存器"),
            };

            for (int s = 0; s < 8; s++)
            {
                ulong offset = 0x10 + (ulong)(0x18 * s);
                dma.Add(R($"S{s}CR", offset, $"Stream {s} 配置寄存器：EN / DIR / CIRC / MINC / PINC"));
                dma.Add(R($"S{s}NDTR", offset + 0x04, $"Stream {s} 数据项数寄存器"));
                dma.Add(R($"S{s}PAR", offset + 0x08, $"Stream {s} 外设地址寄存器"));
                dma.Add(R($"S{s}M0AR", offset + 0x0C, $"Stream {s} 存储器 0 地址寄存器"));
                dma.Add(R($"S{s}M1AR", offset + 0x10, $"Stream {s} 存储器 1 地址寄存器"));
                dma.Add(R($"S{s}FCR", offset + 0x14, $"Stream {s} FIFO 控制寄存器"));
            }

            Add(model, name, "DMA", baseAddress, desc, dma.ToArray());
        }

        Add(model, "EXTI", "EXTI", 0x40013C00, "外部中断 / 事件控制器", new[]
        {
            R("IMR", 0x00, "中断屏蔽寄存器", 0, "read-write", new[]
            {
                F("MR0-22", 0, 23, "中断线屏蔽：0 屏蔽 / 1 开放"),
            }),
            R("EMR", 0x04, "事件屏蔽寄存器", 0, "read-write", new[]
            {
                F("MR0-22", 0, 23, "事件线屏蔽：0 屏蔽 / 1 开放"),
            }),
            R("RTSR", 0x08, "上升沿触发选择寄存器", 0, "read-write", new[]
            {
                F("TR0-22", 0, 23, "上升沿触发使能"),
            }),
            R("FTSR", 0x0C, "下降沿触发选择寄存器", 0, "read-write", new[]
            {
                F("TR0-22", 0, 23, "下降沿触发使能"),
            }),
            R("SWIER", 0x10, "软件中断事件寄存器", 0, "read-write", new[]
            {
                F("SWIER0-22", 0, 23, "软件中断 / 事件触发"),
            }),
            R("PR", 0x14, "挂起寄存器", 0, "read-write", new[]
            {
                F("PR0-22", 0, 23, "中断挂起标志（写 1 清除）"),
            }),
        });

        Add(model, "SYSCFG", "SYSCFG", 0x40013800, "系统配置控制器", new[]
        {
            R("MEMRMP", 0x00, "存储器重映射寄存器"),
            R("PMC", 0x04, "外设模式配置寄存器"),
            R("EXTICR1", 0x08, "外部中断线路 0-3 配置"),
            R("EXTICR2", 0x0C, "外部中断线路 4-7 配置"),
            R("EXTICR3", 0x10, "外部中断线路 8-11 配置"),
            R("EXTICR4", 0x14, "外部中断线路 12-15 配置"),
            R("CMPCR", 0x20, "补偿单元控制寄存器"),
        });

        Add(model, "FLASH", "FLASH", 0x40023C00, "闪存接口", new[]
        {
            R("ACR", 0x00, "访问控制寄存器：LATENCY / PRFTEN / ICEN / DCEN", 0, "read-write", new[]
            {
                F("LATENCY", 0, 3, "Flash 等待周期（随主频设置）"),
                F("PRFTEN", 8, 1, "预取使能"),
                F("ICEN", 9, 1, "指令缓存使能"),
                F("DCEN", 10, 1, "数据缓存使能"),
                F("ICRST", 11, 1, "指令缓存复位"),
                F("DCRST", 12, 1, "数据缓存复位"),
            }),
            R("KEYR", 0x04, "密钥寄存器", 0, "write-only"),
            R("OPTKEYR", 0x08, "选项字节密钥寄存器", 0, "write-only"),
            R("SR", 0x0C, "状态寄存器：EOP / OPERR / WRPERR / PGSERR / BSY", 0, "read-only", new[]
            {
                F("EOP", 0, 1, "操作结束"),
                F("OPERR", 1, 1, "操作错误"),
                F("WRPERR", 4, 1, "写保护错误"),
                F("PGAERR", 5, 1, "编程对齐错误"),
                F("PGPERR", 6, 1, "编程并行错误"),
                F("PGSERR", 7, 1, "编程序列错误"),
                F("BSY", 16, 1, "忙"),
            }),
            R("CR", 0x10, "控制寄存器：PG / SER / MER / SNB / PSIZE / STRT", 0, "read-write", new[]
            {
                F("PG", 0, 1, "编程使能"),
                F("SER", 1, 1, "扇区擦除"),
                F("MER", 2, 1, "全片擦除"),
                F("SNB", 3, 4, "扇区编号"),
                F("PSIZE", 8, 2, "编程位宽：00 x8 / 01 x16 / 10 x32 / 11 x64"),
                F("STRT", 16, 1, "启动擦除 / 编程"),
            }),
            R("OPTCR", 0x14, "选项控制寄存器：nWRP / RDP / BOR / WDG_SW"),
        });

        Add(model, "PWR", "PWR", 0x40007000, "电源控制", new[]
        {
            R("CR", 0x00, "电源控制寄存器：LPDS / PDDS / CWUF / CSBF / DBP / PLS / VOS", 0, "read-write", new[]
            {
                F("LPDS", 0, 1, "低功耗深度睡眠"),
                F("PDDS", 1, 1, "掉电深度睡眠"),
                F("CWUF", 2, 1, "清除唤醒标志"),
                F("CSBF", 3, 1, "清除待机标志"),
                F("PVDE", 4, 1, "电源电压检测使能"),
                F("PLS", 5, 3, "PVD 阈值"),
                F("DBP", 8, 1, "禁用备份域写保护"),
                F("FPDS", 9, 1, "Flash 掉电深度睡眠"),
                F("VOS", 14, 2, "电压调节器输出：01 中 / 10 高 / 11 超调"),
            }),
            R("CSR", 0x04, "电源控制/状态寄存器：WUF / SBF / PVDO / BRR / VOSRDY", 0, "read-write", new[]
            {
                F("WUF", 0, 1, "唤醒标志"),
                F("SBF", 1, 1, "待机标志"),
                F("PVDO", 2, 1, "PVD 输出"),
                F("BRR", 3, 1, "备份调节器就绪"),
                F("BRE", 9, 1, "备份调节器使能"),
                F("VOSRDY", 14, 1, "电压调节器就绪"),
            }),
        });

        Add(model, "IWDG", "IWDG", 0x40003000, "独立看门狗", new[]
        {
            R("KR", 0x00, "密钥寄存器：0x5555 允许写 / 0xAAAA 喂狗 / 0xCCCC 启动"),
            R("PR", 0x04, "预分频寄存器"),
            R("RLR", 0x08, "重装载寄存器"),
            R("SR", 0x0C, "状态寄存器：PVU / RVU / WVU", 0, "read-only", new[]
            {
                F("PVU", 0, 1, "预分频值更新中"),
                F("RVU", 1, 1, "重装载值更新中"),
                F("WVU", 2, 1, "窗口值更新中"),
            }),
        });

        Add(model, "WWDG", "WWDG", 0x40002C00, "窗口看门狗", new[]
        {
            R("CR", 0x00, "控制寄存器：T / WDGA"),
            R("CFR", 0x04, "配置寄存器：W / WDGTB / EWI"),
            R("SR", 0x08, "状态寄存器：EWIF", 0, "read-only", new[]
            {
                F("EWIF", 0, 1, "提前唤醒中断标志"),
            }),
        });

        Add(model, "CRC", "CRC", 0x40023000, "循环冗余校验计算单元", new[]
        {
            R("DR", 0x00, "数据寄存器"),
            R("IDR", 0x04, "独立数据寄存器"),
        });

        Add(model, "DBGMCU", "DBGMCU", 0xE0042000, "调试 MCU 配置（低功耗下保持调试）", new[]
        {
            R("IDCODE", 0x00, "标识码：DEV_ID / REV_ID", 0x10076413, "read-only"),
            R("CR", 0x04, "调试控制寄存器：DBG_SLEEP / DBG_STOP / DBG_STANDBY / TRACE_IOEN", 0, "read-write", new[]
            {
                F("DBG_SLEEP", 0, 1, "睡眠模式下保持调试"),
                F("DBG_STOP", 1, 1, "停止模式下保持调试"),
                F("DBG_STANDBY", 2, 1, "待机模式下保持调试"),
                F("TRACE_IOEN", 5, 1, "跟踪引脚使能"),
                F("TRACE_MODE", 6, 2, "跟踪模式"),
            }),
            R("APB1FZ", 0x08, "APB1 外设调试冻结寄存器"),
            R("APB2FZ", 0x0C, "APB2 外设调试冻结寄存器"),
        });

        return model;
    }

    /// <summary>STM32F1/F3/L1（CRL/CRH 风格 GPIO）：按 RM0008 常用外设。</summary>
    private static SvdModel BuildStm32F1(string modelName)
    {
        var model = new SvdModel
        {
            Name = modelName,
            Version = "RM0008",
            Description = $"{modelName} · 内置常用寄存器表",
        };

        var gpio = new[]
        {
            R("CRL", 0x00, "端口配置低寄存器（引脚 0-7，每引脚 CNF[1:0]+MODE[1:0]）"),
            R("CRH", 0x04, "端口配置高寄存器（引脚 8-15）"),
            R("IDR", 0x08, "端口输入数据寄存器", 0, "read-only"),
            R("ODR", 0x0C, "端口输出数据寄存器"),
            R("BSRR", 0x10, "端口位设置/清除寄存器", 0, "write-only"),
            R("BRR", 0x14, "端口位清除寄存器", 0, "write-only"),
            R("LCKR", 0x18, "端口配置锁定寄存器"),
        };

        string[] gpioNames = { "GPIOA", "GPIOB", "GPIOC", "GPIOD", "GPIOE" };
        for (int i = 0; i < gpioNames.Length; i++)
            Add(model, gpioNames[i], "GPIO", 0x40010800 + (ulong)(0x400 * i), "通用 I/O 端口", gpio);

        Add(model, "RCC", "RCC", 0x40021000, "复位与时钟控制", new[]
        {
            R("CR", 0x00, "时钟控制寄存器：HSION / HSERDY / PLLON / CSSON", 0x00000083, "read-write", new[]
            {
                F("HSION", 0, 1, "内部 8M RC 使能"),
                F("HSIRDY", 1, 1, "内部 8M RC 就绪"),
                F("HSITRIM", 3, 5, "内部 8M RC 校准"),
                F("HSICAL", 8, 8, "内部 8M RC 校准值"),
                F("HSEON", 16, 1, "外部高速时钟使能"),
                F("HSERDY", 17, 1, "外部高速时钟就绪"),
                F("HSEBYP", 18, 1, "外部高速时钟旁路"),
                F("CSSON", 19, 1, "时钟安全系统使能"),
                F("PLLON", 24, 1, "PLL 使能"),
                F("PLLRDY", 25, 1, "PLL 就绪"),
            }),
            R("CFGR", 0x04, "时钟配置寄存器：SW / HPRE / PPRE1 / PPRE2 / PLLMUL"),
            R("CIR", 0x08, "时钟中断寄存器"),
            R("APB2RSTR", 0x0C, "APB2 外设复位寄存器"),
            R("APB1RSTR", 0x10, "APB1 外设复位寄存器"),
            R("AHBENR", 0x14, "AHB 外设时钟使能寄存器"),
            R("APB2ENR", 0x18, "APB2 外设时钟使能寄存器"),
            R("APB1ENR", 0x1C, "APB1 外设时钟使能寄存器"),
            R("BDCR", 0x20, "备份域控制寄存器"),
            R("CSR", 0x24, "控制/状态寄存器"),
        });

        var usart = new[]
        {
            R("SR", 0x00, "状态寄存器：PE / FE / ORE / RXNE / TC / TXE", 0x00C0, "read-only", new[]
            {
                F("PE", 0, 1, "校验错误"),
                F("FE", 1, 1, "帧错误"),
                F("NE", 2, 1, "噪声错误"),
                F("ORE", 3, 1, "溢出错误"),
                F("IDLE", 4, 1, "空闲线路"),
                F("RXNE", 5, 1, "读数据寄存器非空"),
                F("TC", 6, 1, "发送完成"),
                F("TXE", 7, 1, "发送数据寄存器空"),
            }),
            R("DR", 0x04, "数据寄存器"),
            R("BRR", 0x08, "波特率寄存器"),
            R("CR1", 0x0C, "控制寄存器 1：UE / M / PCE / TE / RE"),
            R("CR2", 0x10, "控制寄存器 2：STOP / LINEN"),
            R("CR3", 0x14, "控制寄存器 3：EIE / DMAR / DMAT"),
            R("GTPR", 0x18, "保护时间与预分频寄存器"),
        };

        Add(model, "USART1", "USART", 0x40013800, "通用同步异步收发器 1（APB2）", usart);
        Add(model, "USART2", "USART", 0x40004400, "通用同步异步收发器 2（APB1）", usart);
        Add(model, "USART3", "USART", 0x40004800, "通用同步异步收发器 3（APB1）", usart);
        Add(model, "UART4", "USART", 0x40004C00, "通用异步收发器 4（APB1）", usart);
        Add(model, "UART5", "USART", 0x40005000, "通用异步收发器 5（APB1）", usart);

        var timer = new[]
        {
            R("CR1", 0x00, "控制寄存器 1：CEN / UDIS / OPM / DIR / CMS / ARPE", 0, "read-write", new[]
            {
                F("CEN", 0, 1, "计数器使能"),
                F("UDIS", 1, 1, "更新禁止"),
                F("URS", 2, 1, "更新请求源"),
                F("OPM", 3, 1, "单脉冲模式"),
                F("DIR", 4, 1, "计数方向"),
                F("CMS", 5, 2, "对齐模式"),
                F("ARPE", 7, 1, "自动重装载预装载使能"),
                F("CKD", 8, 2, "时钟分频因子"),
            }),
            R("CR2", 0x04, "控制寄存器 2"),
            R("SMCR", 0x08, "从模式控制寄存器"),
            R("DIER", 0x0C, "DMA/中断使能寄存器"),
            R("SR", 0x10, "状态寄存器：UIF / CC1IF / TIF", 0, "read-write"),
            R("EGR", 0x14, "事件产生寄存器"),
            R("CCMR1", 0x18, "捕获/比较模式寄存器 1"),
            R("CCMR2", 0x1C, "捕获/比较模式寄存器 2"),
            R("CCER", 0x20, "捕获/比较使能寄存器"),
            R("CNT", 0x24, "计数器"),
            R("PSC", 0x28, "预分频器"),
            R("ARR", 0x2C, "自动重装载寄存器"),
            R("RCR", 0x30, "重复计数寄存器"),
            R("CCR1", 0x34, "捕获/比较寄存器 1"),
            R("CCR2", 0x38, "捕获/比较寄存器 2"),
            R("CCR3", 0x3C, "捕获/比较寄存器 3"),
            R("CCR4", 0x40, "捕获/比较寄存器 4"),
            R("BDTR", 0x44, "断路与死区寄存器"),
            R("DCR", 0x48, "DMA 控制寄存器"),
            R("DMAR", 0x4C, "DMA 地址寄存器"),
        };

        Add(model, "TIM1", "TIM", 0x40012C00, "高级控制定时器 1（APB2）", timer);
        Add(model, "TIM2", "TIM", 0x40000000, "通用定时器 2（APB1）", timer);
        Add(model, "TIM3", "TIM", 0x40000400, "通用定时器 3（APB1）", timer);
        Add(model, "TIM4", "TIM", 0x40000800, "通用定时器 4（APB1）", timer);

        var adc = new[]
        {
            R("SR", 0x00, "状态寄存器：AWD / EOC / JEOC / JSTRT / STRT", 0, "read-write"),
            R("CR1", 0x04, "控制寄存器 1：SCAN / EOCIE / AWDEN"),
            R("CR2", 0x08, "控制寄存器 2：ADON / CONT / DMA / EXTTRIG / SWSTART"),
            R("SMPR1", 0x0C, "采样时间寄存器 1"),
            R("SMPR2", 0x10, "采样时间寄存器 2"),
            R("JOFR1", 0x14, "注入通道偏移 1"),
            R("JOFR2", 0x18, "注入通道偏移 2"),
            R("JOFR3", 0x1C, "注入通道偏移 3"),
            R("JOFR4", 0x20, "注入通道偏移 4"),
            R("HTR", 0x24, "看门狗高阈值"),
            R("LTR", 0x28, "看门狗低阈值"),
            R("SQR1", 0x2C, "规则序列寄存器 1"),
            R("SQR2", 0x30, "规则序列寄存器 2"),
            R("SQR3", 0x34, "规则序列寄存器 3"),
            R("JSQR", 0x38, "注入序列寄存器"),
            R("JDR1", 0x3C, "注入数据寄存器 1", 0, "read-only"),
            R("JDR2", 0x40, "注入数据寄存器 2", 0, "read-only"),
            R("JDR3", 0x44, "注入数据寄存器 3", 0, "read-only"),
            R("JDR4", 0x48, "注入数据寄存器 4", 0, "read-only"),
            R("DR", 0x4C, "规则数据寄存器", 0, "read-only"),
        };

        Add(model, "ADC1", "ADC", 0x40012400, "模数转换器 1", adc);
        Add(model, "ADC2", "ADC", 0x40012800, "模数转换器 2", adc);

        var spi = new[]
        {
            R("CR1", 0x00, "控制寄存器 1：CPHA / CPOL / MSTR / BR / SPE"),
            R("CR2", 0x04, "控制寄存器 2"),
            R("SR", 0x08, "状态寄存器：RXNE / TXE / BSY / OVR / MODF", 0x0002, "read-only"),
            R("DR", 0x0C, "数据寄存器"),
            R("CRCPR", 0x10, "CRC 多项式寄存器"),
            R("RXCRCR", 0x14, "接收 CRC 寄存器", 0, "read-only"),
            R("TXCRCR", 0x18, "发送 CRC 寄存器", 0, "read-only"),
            R("I2SCFGR", 0x1C, "I2S 配置寄存器"),
            R("I2SPR", 0x20, "I2S 预分频寄存器"),
        };

        Add(model, "SPI1", "SPI", 0x40013000, "串行外设接口 1（APB2）", spi);
        Add(model, "SPI2", "SPI", 0x40003800, "串行外设接口 2（APB1）", spi);
        Add(model, "SPI3", "SPI", 0x40003C00, "串行外设接口 3（APB1）", spi);

        var i2c = new[]
        {
            R("CR1", 0x00, "控制寄存器 1：PE / START / STOP / ACK"),
            R("CR2", 0x04, "控制寄存器 2：FREQ / ITERREN / ITEVTEN"),
            R("OAR1", 0x08, "自身地址寄存器 1"),
            R("OAR2", 0x0C, "自身地址寄存器 2"),
            R("DR", 0x10, "数据寄存器"),
            R("SR1", 0x14, "状态寄存器 1：SB / ADDR / BTF / RXNE / TXE / AF", 0, "read-only"),
            R("SR2", 0x18, "状态寄存器 2：MSL / BUSY / TRA", 0, "read-only"),
            R("CCR", 0x1C, "时钟控制寄存器"),
            R("TRISE", 0x20, "上升时间寄存器"),
        };

        Add(model, "I2C1", "I2C", 0x40005400, "内部集成电路接口 1", i2c);
        Add(model, "I2C2", "I2C", 0x40005800, "内部集成电路接口 2", i2c);

        Add(model, "FLASH", "FLASH", 0x40022000, "闪存接口", new[]
        {
            R("ACR", 0x00, "访问控制寄存器：LATENCY / PRFTBE / PRFTBS"),
            R("KEYR", 0x04, "密钥寄存器", 0, "write-only"),
            R("OPTKEYR", 0x08, "选项字节密钥寄存器", 0, "write-only"),
            R("SR", 0x0C, "状态寄存器：BSY / PGERR / WRPRTERR / EOP", 0, "read-only"),
            R("CR", 0x10, "控制寄存器：PG / PER / MER / STRT / LOCK"),
            R("AR", 0x14, "地址寄存器"),
            R("OBR", 0x1C, "选项字节寄存器"),
            R("WRPR", 0x20, "写保护寄存器"),
        });

        Add(model, "PWR", "PWR", 0x40007000, "电源控制", new[]
        {
            R("CR", 0x00, "电源控制寄存器：LPDS / PDDS / CWUF / CSBF / DBP / PLS / PVDE"),
            R("CSR", 0x04, "电源控制/状态寄存器：WUF / SBF / PVDO"),
        });

        Add(model, "IWDG", "IWDG", 0x40003000, "独立看门狗", new[]
        {
            R("KR", 0x00, "密钥寄存器", 0, "write-only"),
            R("PR", 0x04, "预分频寄存器"),
            R("RLR", 0x08, "重装载寄存器"),
            R("SR", 0x0C, "状态寄存器：PVU / RVU", 0, "read-only"),
        });

        Add(model, "WWDG", "WWDG", 0x40002C00, "窗口看门狗", new[]
        {
            R("CR", 0x00, "控制寄存器：T / WDGA"),
            R("CFR", 0x04, "配置寄存器：W / WDGTB / EWI"),
            R("SR", 0x08, "状态寄存器：EWIF", 0, "read-only"),
        });

        Add(model, "DBGMCU", "DBGMCU", 0xE0042000, "调试 MCU 配置", new[]
        {
            R("IDCODE", 0x00, "标识码", 0, "read-only"),
            R("CR", 0x04, "调试控制寄存器"),
        });

        AddCortexCore(model);
        return model;
    }

    /// <summary>GD32F1xx / GD32F3xx：外设命名与地址按 GD32 用户手册（CTL/STAT/DATA 风格）。</summary>
    private static SvdModel BuildGd32F1(string modelName)
    {
        var model = new SvdModel
        {
            Name = modelName,
            Version = "GD32 用户手册",
            Description = $"{modelName} · 内置常用寄存器表",
        };

        var gpio = new[]
        {
            R("CTL0", 0x00, "端口配置低寄存器（引脚 0-7，每引脚 CNF[1:0]+MD[1:0]）"),
            R("CTL1", 0x04, "端口配置高寄存器（引脚 8-15）"),
            R("ISTAT", 0x08, "端口输入状态寄存器", 0, "read-only"),
            R("OCTL", 0x0C, "端口输出控制寄存器"),
            R("BOP", 0x10, "端口位操作寄存器：低 16 位置位，高 16 位复位", 0, "write-only"),
            R("BC", 0x14, "端口位清除寄存器", 0, "write-only"),
            R("LOCK", 0x18, "端口配置锁定寄存器"),
        };

        string[] gpioNames = { "GPIOA", "GPIOB", "GPIOC", "GPIOD", "GPIOE" };
        for (int i = 0; i < gpioNames.Length; i++)
            Add(model, gpioNames[i], "GPIO", 0x40010800 + (ulong)(0x400 * i), "通用 I/O 端口", gpio);

        Add(model, "RCC", "RCC", 0x40021000, "复位与时钟控制", new[]
        {
            R("CTL", 0x00, "时钟控制寄存器：IRC16M / HXTAL / PLL 使能与就绪", 0x00000083, "read-write", new[]
            {
                F("IRC16MEN", 0, 1, "内部 16M RC 使能"),
                F("IRC16MRDY", 1, 1, "内部 16M RC 就绪"),
                F("IRC16MSTB", 2, 1, "内部 16M RC 稳定"),
                F("IRC16MADJ", 3, 5, "内部 16M RC 校准"),
                F("IRC16MCAL", 8, 8, "内部 16M RC 校准值"),
                F("HXTALEN", 16, 1, "外部晶振使能"),
                F("HXTALRDY", 17, 1, "外部晶振就绪"),
                F("HXTALBPS", 18, 1, "外部晶振旁路"),
                F("CKMEN", 19, 1, "时钟监测使能"),
                F("PLLEN", 24, 1, "PLL 使能"),
                F("PLLRDY", 25, 1, "PLL 就绪"),
            }),
            R("CFG0", 0x04, "时钟配置寄存器 0：SCS / AHBPSC / APB1PSC / APB2PSC"),
            R("CFG1", 0x08, "时钟配置寄存器 1：PLL 倍频与分频、ADC 预分频"),
            R("INT", 0x0C, "时钟中断寄存器"),
            R("APB2RST", 0x10, "APB2 复位寄存器"),
            R("APB1RST", 0x14, "APB1 复位寄存器"),
            R("AHBEN", 0x18, "AHB 时钟使能寄存器"),
            R("APB2EN", 0x1C, "APB2 时钟使能寄存器"),
            R("APB1EN", 0x20, "APB1 时钟使能寄存器"),
            R("BDCTL", 0x24, "备份域控制寄存器"),
            R("RSTSCK", 0x28, "复位来源与时钟寄存器"),
            R("AHBRST", 0x2C, "AHB 复位寄存器"),
            R("CFG2", 0x30, "时钟配置寄存器 2：USB 预分频、PLL 分频"),
        });

        var usart = new[]
        {
            R("STAT", 0x00, "状态寄存器：PERR / FERR / ORERR / RBNE / TC / TBE", 0x00C0, "read-only", new[]
            {
                F("PERR", 0, 1, "校验错误"),
                F("FERR", 1, 1, "帧错误"),
                F("NERR", 2, 1, "噪声错误"),
                F("ORERR", 3, 1, "溢出错误"),
                F("IDLE", 4, 1, "空闲线路检测"),
                F("RBNE", 5, 1, "读数据缓冲区非空"),
                F("TC", 6, 1, "发送完成"),
                F("TBE", 7, 1, "发送数据缓冲区空"),
                F("LBD", 8, 1, "LIN 断开检测"),
                F("CTS", 9, 1, "CTS 变化"),
            }),
            R("DATA", 0x04, "数据寄存器"),
            R("BAUD", 0x08, "波特率寄存器"),
            R("CTL0", 0x0C, "控制寄存器 0：UEN / M / PCEN / PM / TEN / REN", 0, "read-write", new[]
            {
                F("SBK", 0, 1, "发送断开字符"),
                F("RWU", 1, 1, "接收唤醒"),
                F("REN", 2, 1, "接收使能"),
                F("TEN", 3, 1, "发送使能"),
                F("IDLEIE", 4, 1, "空闲中断使能"),
                F("RBNEIE", 5, 1, "接收非空中断使能"),
                F("TCIE", 6, 1, "发送完成中断使能"),
                F("TBEIE", 7, 1, "发送空中断使能"),
                F("PEIE", 8, 1, "校验错误中断使能"),
                F("PM", 9, 1, "校验位选择：0 偶 / 1 奇"),
                F("PCEN", 10, 1, "校验使能"),
                F("WM", 11, 1, "唤醒方式"),
                F("M", 12, 1, "字长：0 8 位 / 1 9 位"),
                F("UEN", 13, 1, "USART 使能"),
                F("OVER8", 15, 1, "过采样模式"),
            }),
            R("CTL1", 0x10, "控制寄存器 1"),
            R("CTL2", 0x14, "控制寄存器 2"),
            R("GP", 0x18, "保护时间与预分频寄存器"),
        };

        Add(model, "USART0", "USART", 0x40013800, "通用同步异步收发器 0（APB2）", usart);
        Add(model, "USART1", "USART", 0x40004400, "通用同步异步收发器 1（APB1）", usart);
        Add(model, "USART2", "USART", 0x40004800, "通用同步异步收发器 2（APB1）", usart);
        Add(model, "UART3", "USART", 0x40004C00, "通用异步收发器 3（APB1）", usart);
        Add(model, "UART4", "USART", 0x40005000, "通用异步收发器 4（APB1）", usart);

        var timer = new[]
        {
            R("CTL0", 0x00, "控制寄存器 0：CEN / UPDIS / SPM / DIR / CAM / ARSE", 0, "read-write", new[]
            {
                F("CEN", 0, 1, "计数器使能"),
                F("UPDIS", 1, 1, "更新禁止"),
                F("UPS", 2, 1, "更新请求源"),
                F("SPM", 3, 1, "单脉冲模式"),
                F("DIR", 4, 1, "计数方向：0 向上 / 1 向下"),
                F("CAM", 5, 2, "中央对齐模式"),
                F("ARSE", 7, 1, "自动重装载影子使能"),
                F("CKDIV", 8, 2, "时钟分频因子"),
            }),
            R("CTL1", 0x04, "控制寄存器 1"),
            R("SMCFG", 0x08, "从模式配置寄存器"),
            R("DMAINTEN", 0x0C, "DMA 与中断使能寄存器"),
            R("INTF", 0x10, "中断标志寄存器：UPIF / CH0IF / TRGIF", 0, "read-write"),
            R("SWEVG", 0x14, "软件事件产生寄存器"),
            R("CHCTL0", 0x18, "通道控制寄存器 0"),
            R("CHCTL1", 0x1C, "通道控制寄存器 1"),
            R("CHCTL2", 0x20, "通道控制寄存器 2"),
            R("CNT", 0x24, "计数器"),
            R("PSC", 0x28, "预分频器"),
            R("CAR", 0x2C, "计数器自动重装载寄存器"),
            R("CREP", 0x30, "重复计数寄存器"),
            R("CH0CV", 0x34, "通道 0 捕获/比较值"),
            R("CH1CV", 0x38, "通道 1 捕获/比较值"),
            R("CH2CV", 0x3C, "通道 2 捕获/比较值"),
            R("CH3CV", 0x40, "通道 3 捕获/比较值"),
            R("CCHP", 0x44, "通道互补与死区寄存器"),
            R("DMACFG", 0x48, "DMA 配置寄存器"),
            R("DMATB", 0x4C, "DMA 传输基地址寄存器"),
        };

        Add(model, "TIMER0", "TIMER", 0x40012C00, "高级定时器 0（APB2）", timer);
        Add(model, "TIMER1", "TIMER", 0x40000000, "通用定时器 1（APB1）", timer);
        Add(model, "TIMER2", "TIMER", 0x40000400, "通用定时器 2（APB1）", timer);
        Add(model, "TIMER3", "TIMER", 0x40000800, "通用定时器 3（APB1）", timer);
        Add(model, "TIMER4", "TIMER", 0x40000C00, "通用定时器 4（APB1）", timer);
        Add(model, "TIMER5", "TIMER", 0x40001000, "基本定时器 5（APB1）", timer);
        Add(model, "TIMER6", "TIMER", 0x40001400, "基本定时器 6（APB1）", timer);

        var adc = new[]
        {
            R("STAT", 0x00, "状态寄存器：WDE / EOC / EOIC / STIC / STRIC", 0, "read-write", new[]
            {
                F("WDE", 0, 1, "模拟看门狗标志"),
                F("EOC", 1, 1, "规则转换结束"),
                F("EOIC", 2, 1, "注入转换结束"),
                F("STIC", 3, 1, "注入转换已启动"),
                F("STRIC", 4, 1, "规则转换已启动"),
            }),
            R("CTL0", 0x04, "控制寄存器 0"),
            R("CTL1", 0x08, "控制寄存器 1：ADCON / CONT / DMA / RSTCLB"),
            R("SAMPT0", 0x0C, "采样时间寄存器 0（通道 10-17）"),
            R("SAMPT1", 0x10, "采样时间寄存器 1（通道 0-9）"),
            R("IOFF0", 0x14, "注入通道偏移 0"),
            R("IOFF1", 0x18, "注入通道偏移 1"),
            R("IOFF2", 0x1C, "注入通道偏移 2"),
            R("IOFF3", 0x20, "注入通道偏移 3"),
            R("WDHT", 0x24, "看门狗高阈值"),
            R("WDLT", 0x28, "看门狗低阈值"),
            R("RSQ0", 0x2C, "规则序列寄存器 0"),
            R("RSQ1", 0x30, "规则序列寄存器 1"),
            R("RSQ2", 0x34, "规则序列寄存器 2"),
            R("ISQ", 0x38, "注入序列寄存器"),
            R("IDATA0", 0x3C, "注入数据 0", 0, "read-only"),
            R("IDATA1", 0x40, "注入数据 1", 0, "read-only"),
            R("IDATA2", 0x44, "注入数据 2", 0, "read-only"),
            R("IDATA3", 0x48, "注入数据 3", 0, "read-only"),
            R("RDATA", 0x4C, "规则数据寄存器", 0, "read-only"),
        };

        Add(model, "ADC0", "ADC", 0x40012400, "模数转换器 0", adc);
        Add(model, "ADC1", "ADC", 0x40012800, "模数转换器 1", adc);

        var spi = new[]
        {
            R("CTL0", 0x00, "控制寄存器 0：SPIEN / MSTMOD / CKPL / CKPH / PSC", 0, "read-write", new[]
            {
                F("CKPL", 0, 1, "时钟极性"),
                F("CKPH", 1, 1, "时钟相位"),
                F("MSTMOD", 2, 1, "主模式"),
                F("PSC", 3, 3, "波特率预分频"),
                F("SPIEN", 6, 1, "SPI 使能"),
                F("LF", 7, 1, "LSB 优先"),
                F("SWNSS", 8, 1, "内部 NSS 电平"),
                F("SWNSSEN", 9, 1, "软件 NSS 使能"),
                F("RO", 10, 1, "只接收"),
                F("FF16", 11, 1, "16 位数据帧"),
                F("CRCNT", 12, 1, "下一个发送 CRC"),
                F("CRCEN", 13, 1, "硬件 CRC 使能"),
                F("BDOEN", 14, 1, "双向输出使能"),
                F("BDEN", 15, 1, "双向模式使能"),
            }),
            R("CTL1", 0x04, "控制寄存器 1"),
            R("STAT", 0x08, "状态寄存器：RBNE / TBE / CONFERR / RXORERR / TRANS", 0x0002, "read-only"),
            R("DATA", 0x0C, "数据寄存器"),
            R("CRCPOLY", 0x10, "CRC 多项式寄存器"),
            R("RCRC", 0x14, "接收 CRC 寄存器", 0, "read-only"),
            R("TCRC", 0x18, "发送 CRC 寄存器", 0, "read-only"),
            R("I2SCTL", 0x1C, "I2S 控制寄存器"),
            R("I2SPSC", 0x20, "I2S 预分频寄存器"),
        };

        Add(model, "SPI0", "SPI", 0x40013000, "串行外设接口 0（APB2）", spi);
        Add(model, "SPI1", "SPI", 0x40003800, "串行外设接口 1（APB1）", spi);
        Add(model, "SPI2", "SPI", 0x40003C00, "串行外设接口 2（APB1）", spi);

        var i2c = new[]
        {
            R("CTL0", 0x00, "控制寄存器 0：I2CEN / START / STOP / ACKEN"),
            R("CTL1", 0x04, "控制寄存器 1"),
            R("SADDR0", 0x08, "自身地址寄存器 0"),
            R("SADDR1", 0x0C, "自身地址寄存器 1"),
            R("DATA", 0x10, "数据寄存器"),
            R("STAT0", 0x14, "状态寄存器 0：SBSEND / ADDSEND / BTC / RBNE / TBE", 0, "read-only"),
            R("STAT1", 0x18, "状态寄存器 1：MASTER / I2CBSY / TRS", 0, "read-only"),
            R("CKCFG", 0x1C, "时钟配置寄存器"),
            R("TRISE", 0x20, "上升时间寄存器"),
        };

        Add(model, "I2C0", "I2C", 0x40005400, "内部集成电路接口 0", i2c);
        Add(model, "I2C1", "I2C", 0x40005800, "内部集成电路接口 1", i2c);

        AddCortexCore(model);
        return model;
    }

    /// <summary>LPC17xx（LPC1768 等）：外设按 NXP LPC17xx 用户手册。</summary>
    private static SvdModel BuildLpc17xx(string modelName)
    {
        var model = new SvdModel
        {
            Name = modelName,
            Version = "LPC17xx 用户手册",
            Description = $"{modelName} · 内置常用寄存器表",
        };

        var fio = new[]
        {
            R("FIODIR", 0x00, "快速 GPIO 方向寄存器：1 输出 / 0 输入"),
            R("FIOMASK", 0x10, "快速 GPIO 掩码寄存器"),
            R("FIOPIN", 0x14, "快速 GPIO 引脚值寄存器"),
            R("FIOSET", 0x18, "快速 GPIO 置位寄存器（写 1 置位）", 0, "write-only"),
            R("FIOCLR", 0x1C, "快速 GPIO 清零寄存器（写 1 清零）", 0, "write-only"),
        };

        for (int i = 0; i < 5; i++)
            Add(model, $"FIO{i}", "GPIO", 0x2009C000 + (ulong)(0x20 * i), $"快速 GPIO 端口 {i}", fio);

        var uart = new[]
        {
            R("RBR/THR/DLL", 0x00, "接收缓冲 / 发送保持 / 除数低字节（DLAB 决定）"),
            R("DLM/IER", 0x04, "除数高字节 / 中断使能（DLAB 决定）"),
            R("IIR/FCR", 0x08, "中断标识 / FIFO 控制"),
            R("LCR", 0x0C, "线路控制寄存器：字长 / 停止位 / 校验 / DLAB"),
            R("MCR", 0x10, "调制解调器控制寄存器"),
            R("LSR", 0x14, "线路状态寄存器：RDR / THRE / TEMT / OE / PE / FE", 0x0060, "read-only"),
            R("MSR", 0x18, "调制解调器状态寄存器"),
            R("SCR", 0x1C, "暂存寄存器"),
            R("ACR", 0x20, "自动波特率控制寄存器"),
            R("ICR", 0x24, "IrDA 控制寄存器"),
            R("FDR", 0x28, "小数分频寄存器"),
            R("TER", 0x30, "发送使能寄存器"),
        };

        Add(model, "UART0", "UART", 0x4000C000, "UART0（AHB 外设）", uart);
        Add(model, "UART1", "UART", 0x40010000, "UART1（AHB 外设）", uart);
        Add(model, "UART2", "UART", 0x40098000, "UART2（APB 外设）", uart);
        Add(model, "UART3", "UART", 0x4009C000, "UART3（APB 外设）", uart);

        var timer = new[]
        {
            R("IR", 0x00, "中断寄存器：MR0-3 匹配 / CR0-1 捕获"),
            R("TCR", 0x04, "定时器控制寄存器：CEN / CRST / CTCR 模式"),
            R("TC", 0x08, "定时器计数器", 0, "read-only"),
            R("PR", 0x0C, "预分频寄存器"),
            R("PC", 0x10, "预分频计数器", 0, "read-only"),
            R("MCR", 0x14, "匹配控制寄存器"),
            R("MR0", 0x18, "匹配寄存器 0"),
            R("MR1", 0x1C, "匹配寄存器 1"),
            R("MR2", 0x20, "匹配寄存器 2"),
            R("MR3", 0x24, "匹配寄存器 3"),
            R("CCR", 0x28, "捕获控制寄存器"),
            R("CR0", 0x2C, "捕获寄存器 0", 0, "read-only"),
            R("CR1", 0x30, "捕获寄存器 1", 0, "read-only"),
            R("EMR", 0x3C, "外部匹配寄存器"),
            R("CTCR", 0x40, "计数控制寄存器"),
            R("PWMCON", 0x74, "PWM 控制寄存器"),
        };

        for (int i = 0; i < 4; i++)
        {
            ulong baseAddress = i switch
            {
                0 => 0x40004000UL,
                1 => 0x40008000UL,
                2 => 0x40090000UL,
                _ => 0x40094000UL,
            };
            Add(model, $"TIMER{i}", "TIMER", baseAddress, $"32 位定时器 {i}", timer);
        }

        Add(model, "ADC", "ADC", 0x40034000, "12 位逐次逼近 ADC", new[]
        {
            R("CR", 0x00, "A/D 控制寄存器：PDN / BURST / CLKS / START"),
            R("GDR", 0x04, "全局数据寄存器（含通道号）", 0, "read-only"),
            R("INTEN", 0x0C, "中断使能寄存器"),
            R("DR0", 0x10, "通道 0 数据寄存器", 0, "read-only"),
            R("DR1", 0x14, "通道 1 数据寄存器", 0, "read-only"),
            R("DR2", 0x18, "通道 2 数据寄存器", 0, "read-only"),
            R("DR3", 0x1C, "通道 3 数据寄存器", 0, "read-only"),
            R("DR4", 0x20, "通道 4 数据寄存器", 0, "read-only"),
            R("DR5", 0x24, "通道 5 数据寄存器", 0, "read-only"),
            R("DR6", 0x28, "通道 6 数据寄存器", 0, "read-only"),
            R("DR7", 0x2C, "通道 7 数据寄存器", 0, "read-only"),
            R("STAT", 0x30, "状态寄存器：DONE / OVERRUN / ADINT", 0, "read-only"),
        });

        Add(model, "PINCONNECT", "PINCONNECT", 0x4002C000, "引脚功能选择（PINSEL0-10，每 2 位一个引脚）", new[]
        {
            R("PINSEL0", 0x00, "引脚 P0.0-P0.15 功能选择"),
            R("PINSEL1", 0x04, "引脚 P0.16-P0.31 功能选择"),
            R("PINSEL2", 0x08, "引脚 P1.16-P1.31 功能选择"),
            R("PINSEL3", 0x0C, "引脚 P1.0-P1.15 功能选择"),
            R("PINSEL4", 0x10, "引脚 P2.0-P2.15 功能选择"),
            R("PINSEL5", 0x14, "引脚 P2.16-P2.31 功能选择"),
            R("PINSEL6", 0x18, "引脚 P3.0-P3.15 功能选择"),
            R("PINSEL7", 0x1C, "引脚 P3.16-P3.31 功能选择"),
            R("PINSEL8", 0x20, "引脚 P4.0-P4.15 功能选择"),
            R("PINSEL9", 0x24, "引脚 P4.16-P4.31 功能选择"),
            R("PINSEL10", 0x28, "引脚 P5.0-P5.3 功能选择"),
        });

        Add(model, "SYSCON", "SYSCON", 0x400FC000, "系统控制（时钟 / 掉电 / 外设功耗）", new[]
        {
            R("FLASHCFG", 0x000, "Flash 加速配置"),
            R("PLL0CON", 0x080, "PLL0 控制寄存器"),
            R("PLL0CFG", 0x084, "PLL0 配置寄存器：MSEL / NSEL"),
            R("PLL0STAT", 0x088, "PLL0 状态寄存器", 0, "read-only"),
            R("PLL0FEED", 0x08C, "PLL0 馈送寄存器（0xAA / 0x55）", 0, "write-only"),
            R("PLL1CON", 0x0A0, "PLL1 控制寄存器"),
            R("PLL1CFG", 0x0A4, "PLL1 配置寄存器"),
            R("PLL1STAT", 0x0A8, "PLL1 状态寄存器", 0, "read-only"),
            R("PLL1FEED", 0x0AC, "PLL1 馈送寄存器", 0, "write-only"),
            R("PCON", 0x0C0, "掉电控制寄存器：IDLE / PD"),
            R("PCONP", 0x0C4, "外设功耗控制寄存器"),
            R("CLKSRCSEL", 0x10C, "时钟源选择寄存器"),
            R("PCLKSEL0", 0x1A8, "外设时钟分频选择 0"),
            R("PCLKSEL1", 0x1AC, "外设时钟分频选择 1"),
        });

        var ssp = new[]
        {
            R("CR0", 0x00, "控制寄存器 0：DSS / FRF / CPOL / CPHA / SCR"),
            R("CR1", 0x04, "控制寄存器 1：LBM / SSE / MS / SOD"),
            R("DR", 0x08, "数据寄存器"),
            R("SR", 0x0C, "状态寄存器：TFE / TNF / RNE / BSY", 0, "read-only"),
            R("CPSR", 0x10, "时钟预分频寄存器"),
            R("IMSC", 0x14, "中断屏蔽寄存器"),
            R("RIS", 0x18, "原始中断状态", 0, "read-only"),
            R("MIS", 0x1C, "屏蔽后中断状态", 0, "read-only"),
            R("ICR", 0x20, "中断清除寄存器", 0, "write-only"),
            R("DMACR", 0x24, "DMA 控制寄存器"),
        };

        Add(model, "SSP0", "SSP", 0x40088000, "同步串行端口 0", ssp);
        Add(model, "SSP1", "SSP", 0x40030000, "同步串行端口 1", ssp);

        AddCortexCore(model);
        return model;
    }

    /// <summary>i.MX RT10xx（MIMXRT1062 等）：按 RT1060 参考手册精简收录。</summary>
    private static SvdModel BuildImxrt(string modelName)
    {
        var model = new SvdModel
        {
            Name = modelName,
            Version = "RT1060 参考手册",
            Description = $"{modelName} · 内置常用寄存器表（精简）",
        };

        var gpio = new[]
        {
            R("DR", 0x00, "数据寄存器"),
            R("GDIR", 0x04, "方向寄存器：1 输出 / 0 输入"),
            R("PSR", 0x08, "引脚状态寄存器", 0, "read-only"),
            R("ICR1", 0x0C, "中断配置寄存器 1（引脚 0-15）"),
            R("ICR2", 0x10, "中断配置寄存器 2（引脚 16-31）"),
            R("IMR", 0x14, "中断屏蔽寄存器"),
            R("ISR", 0x18, "中断状态寄存器"),
            R("EDGE_SEL", 0x1C, "边沿选择寄存器"),
            R("DR_SET", 0x84, "数据置位寄存器", 0, "write-only"),
            R("DR_CLEAR", 0x88, "数据清零寄存器", 0, "write-only"),
            R("DR_TOGGLE", 0x8C, "数据翻转寄存器", 0, "write-only"),
        };

        ulong[] gpioBases = { 0x401B8000, 0x401BC000, 0x401C0000, 0x401C4000, 0x400C0000 };
        for (int i = 0; i < gpioBases.Length; i++)
            Add(model, $"GPIO{i + 1}", "GPIO", gpioBases[i], $"通用 I/O 端口 {i + 1}", gpio);

        var lpuart = new[]
        {
            R("VERID", 0x00, "版本 ID", 0, "read-only"),
            R("PARAM", 0x04, "参数寄存器", 0, "read-only"),
            R("GLOBAL", 0x08, "全局控制寄存器：RST"),
            R("PINCFG", 0x0C, "引脚配置寄存器"),
            R("BAUD", 0x10, "波特率寄存器（OSR / SBR）"),
            R("STAT", 0x14, "状态寄存器：TDRE / TC / RDRF / IDLE / OR / NF / FE / PF", 0x00C00000, "read-only"),
            R("CTRL", 0x18, "控制寄存器：TE / RE / RIE / TIE / M / PE / PT"),
            R("DATA", 0x1C, "数据寄存器"),
            R("MATCH", 0x20, "匹配寄存器"),
            R("MODIR", 0x24, "调制解调器 / IrDA 寄存器"),
            R("FIFO", 0x28, "FIFO 控制寄存器"),
            R("WATER", 0x2C, "水位寄存器"),
        };

        for (int i = 0; i < 8; i++)
            Add(model, $"LPUART{i + 1}", "LPUART", 0x40184000 + (ulong)(0x4000 * i), $"低功耗 UART {i + 1}", lpuart);

        Add(model, "CCM", "CCM", 0x400FC000, "时钟控制器（CCM）", new[]
        {
            R("CCR", 0x00, "时钟控制寄存器：PMU 相关"),
            R("CCDR", 0x04, "时钟控制分频寄存器（CORE / BUS / IPG）"),
            R("CSR", 0x08, "时钟状态寄存器"),
            R("CCSR", 0x0C, "时钟控制状态寄存器"),
            R("CACRR", 0x10, "ARM 内核时钟分频寄存器"),
            R("CBCDR", 0x14, "总线时钟分频寄存器"),
            R("CBCMR", 0x18, "总线时钟复用寄存器"),
            R("CSCMR1", 0x1C, "串行时钟复用 1"),
            R("CSCMR2", 0x20, "串行时钟复用 2"),
            R("CSCDR1", 0x24, "串行时钟分频 1（UART / LPUART 时钟）"),
            R("CS1CDR", 0x28, "SAI1 时钟分频"),
            R("CS2CDR", 0x2C, "SAI2 时钟分频"),
            R("CDCDR", 0x30, "SPDIF / ESAI 时钟分频"),
            R("CCGR0", 0x68, "时钟门控寄存器 0"),
            R("CCGR1", 0x6C, "时钟门控寄存器 1"),
            R("CCGR2", 0x70, "时钟门控寄存器 2"),
            R("CCGR3", 0x74, "时钟门控寄存器 3"),
            R("CCGR4", 0x78, "时钟门控寄存器 4"),
            R("CCGR5", 0x7C, "时钟门控寄存器 5"),
            R("CCGR6", 0x80, "时钟门控寄存器 6"),
        });

        var lpspi = new[]
        {
            R("VERID", 0x00, "版本 ID", 0, "read-only"),
            R("PARAM", 0x04, "参数寄存器（FIFO 深度）", 0, "read-only"),
            R("CR", 0x10, "控制寄存器：MEN / RST / DBGEN"),
            R("SR", 0x14, "状态寄存器：TDF / RDF / TCF / FCF / WCF", 0, "read-only"),
            R("IER", 0x18, "中断使能寄存器"),
            R("DER", 0x1C, "DMA 使能寄存器"),
            R("CFGR0", 0x20, "配置寄存器 0（主机）"),
            R("CFGR1", 0x24, "配置寄存器 1：MASTER / CPOL / CPHA / PRESCALE / AUTOPCS"),
            R("DMR0", 0x28, "数据匹配寄存器 0"),
            R("DMR1", 0x2C, "数据匹配寄存器 1"),
            R("CCR", 0x30, "时钟配置寄存器（SCKDIV / DBT / SCKPCS）"),
            R("FCR", 0x34, "FIFO 控制寄存器（RXWATER / TXWATER）"),
            R("FSR", 0x38, "FIFO 状态寄存器", 0, "read-only"),
            R("TCR", 0x3C, "发送命令寄存器（FRAMESZ / RXMSK / TXMSK / PCS）"),
            R("TDR", 0x40, "发送数据寄存器", 0, "write-only"),
            R("RSR", 0x44, "接收状态寄存器", 0, "read-only"),
            R("RDR", 0x48, "接收数据寄存器", 0, "read-only"),
        };

        for (int i = 0; i < 4; i++)
            Add(model, $"LPSPI{i + 1}", "LPSPI", 0x40394000 + (ulong)(0x4000 * i), $"低功耗 SPI {i + 1}", lpspi);

        var lpi2c = new[]
        {
            R("VERID", 0x00, "版本 ID", 0, "read-only"),
            R("PARAM", 0x04, "参数寄存器（FIFO 深度）", 0, "read-only"),
            R("MCR", 0x10, "主机控制寄存器：MEN / RST / RTF / RRF"),
            R("MSR", 0x14, "主机状态寄存器：TDF / RDF / EPF / SDF / NDF / ALF / FEF", 0, "read-only"),
            R("MIER", 0x18, "主机中断使能寄存器"),
            R("MDER", 0x1C, "主机 DMA 使能寄存器"),
            R("MCFGR0", 0x20, "主机配置 0"),
            R("MCFGR1", 0x24, "主机配置 1：PRESCALE / AUTOSTOP / IGNACK"),
            R("MCFGR2", 0x28, "主机配置 2（总线空闲 / 滤波）"),
            R("MCFGR3", 0x2C, "主机配置 3（引脚终止）"),
            R("MDMR", 0x30, "主机数据匹配寄存器"),
            R("MCCR0", 0x38, "主机时钟配置 0（CLKLO / CLKHI / SETHOLD / DATAVD）"),
            R("MCCR1", 0x3C, "主机时钟配置 1"),
            R("MFCR", 0x40, "主机 FIFO 控制寄存器"),
            R("MFSR", 0x44, "主机 FIFO 状态寄存器", 0, "read-only"),
            R("MTDR", 0x48, "主机发送数据寄存器", 0, "write-only"),
            R("MRDR", 0x4C, "主机接收数据寄存器", 0, "read-only"),
            R("SCR", 0x110, "从机控制寄存器"),
            R("SSR", 0x114, "从机状态寄存器", 0, "read-only"),
            R("SIER", 0x118, "从机中断使能寄存器"),
            R("SDER", 0x11C, "从机 DMA 使能寄存器"),
            R("SCFGR0", 0x120, "从机配置 0"),
            R("SCFGR1", 0x124, "从机配置 1"),
            R("SCFGR2", 0x128, "从机配置 2"),
            R("SAMR", 0x12C, "从机地址匹配寄存器"),
            R("SASR", 0x130, "从机地址状态寄存器", 0, "read-only"),
            R("STAR", 0x134, "从机发送 ACK 寄存器"),
            R("STDR", 0x138, "从机发送数据寄存器", 0, "write-only"),
            R("SRDR", 0x13C, "从机接收数据寄存器", 0, "read-only"),
        };

        for (int i = 0; i < 4; i++)
            Add(model, $"LPI2C{i + 1}", "LPI2C", 0x403F0000 + (ulong)(0x4000 * i), $"低功耗 I2C {i + 1}", lpi2c);

        var gpt = new[]
        {
            R("CR", 0x00, "控制寄存器：EN / ENMOD / DBGEN / WAITEN / DOZEEN / CLKSRC / FRR"),
            R("PR", 0x04, "预分频寄存器：PRESCALER / PRESCALER24M"),
            R("SR", 0x08, "状态寄存器：OF1 / OF2 / OF3 / ROV", 0, "read-only"),
            R("IR", 0x0C, "中断寄存器：OF1IE / OF2IE / OF3IE / IF1IE / IF2IE"),
            R("OCR1", 0x10, "输出比较寄存器 1"),
            R("OCR2", 0x14, "输出比较寄存器 2"),
            R("OCR3", 0x18, "输出比较寄存器 3"),
            R("ICR1", 0x1C, "输入捕获寄存器 1", 0, "read-only"),
            R("ICR2", 0x20, "输入捕获寄存器 2", 0, "read-only"),
            R("CNT", 0x24, "计数器"),
        };

        Add(model, "GPT1", "GPT", 0x401EC000, "通用定时器 1", gpt);
        Add(model, "GPT2", "GPT", 0x401F0000, "通用定时器 2", gpt);

        var adc = new[]
        {
            R("HC0", 0x00, "转换控制寄存器 0（通道 0-3）"),
            R("HC1", 0x04, "转换控制寄存器 1（通道 4-7）"),
            R("HC2", 0x08, "转换控制寄存器 2（通道 8-11）"),
            R("HC3", 0x0C, "转换控制寄存器 3（通道 12-15）"),
            R("GC", 0x10, "通用控制寄存器：ADTRG / AVGS / ADACKEN / ADHSC / REFSEL"),
            R("GS", 0x14, "通用状态寄存器", 0, "read-only"),
            R("CV", 0x18, "比较值寄存器"),
            R("OFS", 0x1C, "偏移寄存器"),
            R("CAL", 0x20, "校准寄存器"),
            R("R0", 0x40, "通道 0 结果寄存器", 0, "read-only"),
            R("R1", 0x44, "通道 1 结果寄存器", 0, "read-only"),
            R("R2", 0x48, "通道 2 结果寄存器", 0, "read-only"),
            R("R3", 0x4C, "通道 3 结果寄存器", 0, "read-only"),
            R("R4", 0x50, "通道 4 结果寄存器", 0, "read-only"),
            R("R5", 0x54, "通道 5 结果寄存器", 0, "read-only"),
            R("R6", 0x58, "通道 6 结果寄存器", 0, "read-only"),
            R("R7", 0x5C, "通道 7 结果寄存器", 0, "read-only"),
        };

        Add(model, "ADC1", "ADC", 0x400C4000, "模数转换器 1", adc);
        Add(model, "ADC2", "ADC", 0x400C8000, "模数转换器 2", adc);

        var pwm = new[]
        {
            R("PWMCR", 0x00, "PWM 控制寄存器：FWM / PRESCALER / CLKSRC / PWMAEN / PWMBEN"),
            R("PWMSR", 0x04, "PWM 状态寄存器：FIFO 空/满 / 重载", 0, "read-only"),
            R("PWMIR", 0x08, "PWM 中断寄存器"),
            R("PWMSAR", 0x0C, "PWM 采样寄存器（占空比）"),
            R("PWMPR", 0x10, "PWM 周期寄存器"),
            R("PWMCNT", 0x14, "PWM 计数器", 0, "read-only"),
            R("PWMPVAL", 0x18, "PWM 当前计数值"),
            R("PWMVAL0", 0x1C, "PWM 捕获值 0"),
            R("PWMVAL1", 0x20, "PWM 捕获值 1"),
        };

        for (int i = 0; i < 4; i++)
            Add(model, $"PWM{i + 1}", "PWM", 0x403DC000 + (ulong)(0x4000 * i), $"PWM 控制器 {i + 1}", pwm);

        var edma = new List<Reg>
        {
            R("CR", 0x00, "DMA 控制寄存器：EDBG"),
            R("ES", 0x04, "DMA 错误状态寄存器", 0, "read-only"),
            R("ERQ", 0x0C, "DMA 使能请求寄存器"),
            R("EEI", 0x14, "使能错误中断寄存器"),
            R("CEEI", 0x18, "清除使能错误中断", 0, "write-only"),
            R("SEEI", 0x19, "设置使能错误中断", 0, "write-only"),
            R("CERQ", 0x1A, "清除使能请求", 0, "write-only"),
            R("SERQ", 0x1B, "设置使能请求", 0, "write-only"),
            R("CDNE", 0x1C, "清除完成中断", 0, "write-only"),
            R("SSRT", 0x1D, "设置启动位", 0, "write-only"),
            R("CERR", 0x1E, "清除错误中断", 0, "write-only"),
            R("CINT", 0x1F, "清除中断", 0, "write-only"),
            R("INT", 0x24, "中断状态寄存器", 0, "read-only"),
            R("ERR", 0x2C, "错误状态寄存器", 0, "read-only"),
            R("HRS", 0x34, "硬件请求状态寄存器", 0, "read-only"),
            R("EARS", 0x44, "使能自动停止请求寄存器"),
        };

        // TCD（传输控制描述符）：每通道 0x20 字节，从 0x1000 起
        for (int i = 0; i < 4; i++)
        {
            ulong offset = 0x1000 + (ulong)(0x20 * i);
            edma.Add(R($"TCD{i}_SADDR", offset, $"通道 {i} 源地址"));
            edma.Add(R($"TCD{i}_SOFF", offset + 0x04, $"通道 {i} 源地址偏移"));
            edma.Add(R($"TCD{i}_ATTR", offset + 0x06, $"通道 {i} 传输属性（位宽 / 带宽）"));
            edma.Add(R($"TCD{i}_NBYTES", offset + 0x08, $"通道 {i} 每次传输字节数"));
            edma.Add(R($"TCD{i}_SLAST", offset + 0x0C, $"通道 {i} 源地址最后调整"));
            edma.Add(R($"TCD{i}_DADDR", offset + 0x10, $"通道 {i} 目的地址"));
            edma.Add(R($"TCD{i}_DOFF", offset + 0x14, $"通道 {i} 目的地址偏移"));
            edma.Add(R($"TCD{i}_CITER", offset + 0x16, $"通道 {i} 当前迭代次数"));
            edma.Add(R($"TCD{i}_DLAST", offset + 0x18, $"通道 {i} 目的地址最后调整"));
            edma.Add(R($"TCD{i}_CSR", offset + 0x1C, $"通道 {i} 控制与状态（START / DREQ / INTMAJOR）"));
            edma.Add(R($"TCD{i}_BITER", offset + 0x1E, $"通道 {i} 起始迭代次数"));
        }

        Add(model, "DMA0", "eDMA", 0x400E8000, "增强型 DMA 控制器（32 通道）", edma.ToArray());

        AddCortexCore(model);
        return model;
    }

    /// <summary>未收录的芯片：只提供内核 SCB（硬故障分析）+ 说明。</summary>
    private static SvdModel BuildGenericCortexM(string device)
    {
        var model = new SvdModel
        {
            Name = device,
            Version = "Cortex-M 内核",
            Description = $"{device} · 暂未收录外设寄存器，仅提供内核 SCB",
        };

        AddCortexCore(model);
        return model;
    }

    /// <summary>内核系统控制块（各 Cortex-M 芯片通用）。</summary>
    private static void AddCortexCore(SvdModel model)
    {
        Add(model, "SCB", "内核", 0xE000ED00, "系统控制块（HardFault 分析核心）", new[]
        {
            R("CPUID", 0x00, "CPUID 基址寄存器", 0, "read-only"),
            R("ICSR", 0x04, "中断控制与状态寄存器：VECTACTIVE / VECTPENDING"),
            R("VTOR", 0x08, "向量表偏移寄存器"),
            R("AIRCR", 0x0C, "应用中断与复位控制寄存器：SYSRESETREQ"),
            R("SCR", 0x10, "系统控制寄存器：SLEEPONEXIT / SLEEPDEEP"),
            R("CCR", 0x14, "配置与控制寄存器：STKALIGN / DIV_0_TRP"),
            R("SHPR1", 0x18, "系统异常优先级 1"),
            R("SHPR2", 0x1C, "系统异常优先级 2"),
            R("SHPR3", 0x20, "系统异常优先级 3"),
            R("SHCSR", 0x24, "系统处理控制与状态寄存器"),
            R("CFSR", 0x28, "可配置故障状态寄存器", 0, "read-write", new[]
            {
                F("IACCVIOL", 0, 1, "取指访问违例"),
                F("DACCVIOL", 1, 1, "数据访问违例"),
                F("MUNSTKERR", 3, 1, "出栈时存储器管理错误"),
                F("MSTKERR", 4, 1, "入栈时存储器管理错误"),
                F("MMARVALID", 7, 1, "MMFAR 有效"),
                F("IBUSERR", 8, 1, "取指总线错误"),
                F("PRECISERR", 9, 1, "精确数据总线错误"),
                F("IMPRECISERR", 10, 1, "不精确数据总线错误"),
                F("UNSTKERR", 11, 1, "出栈总线错误"),
                F("STKERR", 12, 1, "入栈总线错误"),
                F("BFARVALID", 15, 1, "BFAR 有效"),
                F("UNDEFINSTR", 16, 1, "未定义指令"),
                F("INVSTATE", 17, 1, "非法状态"),
                F("INVPC", 18, 1, "非法 PC 装载"),
                F("NOCP", 19, 1, "协处理器不可用"),
                F("UNALIGNED", 24, 1, "非对齐访问"),
                F("DIVBYZERO", 25, 1, "除零"),
            }),
            R("HFSR", 0x2C, "硬故障状态寄存器", 0, "read-write", new[]
            {
                F("VECTTBL", 1, 1, "向量表读取错误"),
                F("FORCED", 30, 1, "由其他故障升级"),
                F("DEBUGEVT", 31, 1, "调试事件"),
            }),
            R("DFSR", 0x30, "调试故障状态寄存器"),
            R("MMFAR", 0x34, "存储器管理故障地址"),
            R("BFAR", 0x38, "总线故障地址"),
            R("AFSR", 0x3C, "辅助故障状态寄存器"),
        });
    }

    private static void Add(SvdModel model, string name, string group, ulong baseAddress,
        string description, Reg[] registers)
    {
        var peripheral = new SvdPeripheral
        {
            Name = name,
            GroupName = group,
            BaseAddress = baseAddress,
            Description = description,
        };

        foreach (var reg in registers)
        {
            var register = new SvdRegister
            {
                Name = reg.Name,
                BaseAddress = baseAddress,
                Offset = reg.Offset,
                ResetValue = reg.Reset,
                Access = reg.Access,
                Description = reg.Desc,
            };

            foreach (var field in reg.Fields ?? Array.Empty<Field>())
            {
                register.Fields.Add(new SvdField
                {
                    Name = field.Name,
                    BitOffset = field.Offset,
                    BitWidth = field.Width,
                    Description = field.Desc,
                });
            }

            peripheral.Registers.Add(register);
        }

        model.Peripherals.Add(peripheral);
    }
}
