using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OneBot.CommandRoute.Attributes;
using OneBot.CommandRoute.Command;
using OneBot.CommandRoute.Events;
using OneBot.CommandRoute.Lexer;
using OneBot.CommandRoute.Models.Entities;
using Sora.EventArgs.SoraEvent;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using OneBot.CommandRoute.Models;

namespace OneBot.CommandRoute.Services.Implements
{
    /// <summary>
    /// 指令路由服务
    /// </summary>
    public class CommandService : ICommandService
    {
        /// <summary>`
        /// 服务容器
        /// </summary>
        private readonly IServiceProvider _serviceProvider;

        /// <summary>
        /// 指令匹配树根节点
        /// </summary>
        private readonly MatchingNode _matchingRootNode;

        /// <summary>
        /// 日志
        /// </summary>
        private ILogger<CommandService> _logger;

        /// <summary>
        /// CQ:Json 路由
        /// </summary>
        private ICQJsonRouterService? _jsonRouterService;

        /// <summary>
        /// 事件中心
        /// </summary>
        public EventManager Event { get; set; }

        public CommandService(IServiceProvider serviceProvider, ILogger<CommandService> logger)
        {
            _serviceProvider = serviceProvider;
            _jsonRouterService = _serviceProvider.GetService<ICQJsonRouterService>();
            _logger = logger;
            Event = new EventManager();

            var routeConfiguration = serviceProvider.GetService<IOneBotCommandRouteConfiguration>() ??
                                     new DefaultOneBotCommandRouteConfiguration();
            _matchingRootNode = new MatchingNode(routeConfiguration) { IsRoot = true };
        }

        public ValueTask HandleEvent<T>(object sender, OneBotContext<T> oneBotContext) where T : BaseSoraEventArgs
        {
            var eventArgs = oneBotContext.SoraEventArgs;

            if (eventArgs is GroupMessageEventArgs groupMessageEventArgs)
            {
                if (groupMessageEventArgs.IsSelfMessage)
                {
                    return EventOnSelfMessage(sender, (oneBotContext as OneBotContext<GroupMessageEventArgs>)!);
                }
                else
                {
                    return EventOnGroupMessage(sender, (oneBotContext as OneBotContext<GroupMessageEventArgs>)!);
                }
            }
            else if (eventArgs is PrivateMessageEventArgs privateMessageEventArgs)
            {
                return EventOnPrivateMessage(sender, (oneBotContext as OneBotContext<PrivateMessageEventArgs>)!);
            }
            else
            {
                return OnGeneralEvent(sender, oneBotContext, eventArgs);
            }
        }

        #region 事件处理

        /// <summary>
        /// 登录账号发送消息事件
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="oneBotContext"></param>
        /// <returns></returns>
        private ValueTask EventOnSelfMessage(object sender, OneBotContext<GroupMessageEventArgs> oneBotContext)
        {     
            Exception? exception = null;
            try
            {
                Event.FireSelfMessage(oneBotContext);
            }
            catch (Exception e1)
            {
                exception = e1;
            }

            if (exception != null)
            {
                return EventOnException(sender, oneBotContext, exception);
            }
            return ValueTask.CompletedTask;
        }

        /// <summary>
        /// 异常处理
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="oneBotContext"></param>
        /// <param name="exception"></param>
        public ValueTask EventOnException<T>(object sender, OneBotContext<T> oneBotContext, Exception exception)
            where T : BaseSoraEventArgs
        {
            var e = oneBotContext.SoraEventArgs;
            try
            {
                if (!Event.FireException(oneBotContext, exception))
                {
                    _logger.LogError(
                        $"{exception.Message} : \n" +
                        $"{exception.StackTrace}"
                    );
                }
            }
            catch (Exception e1)
            {
                _logger.LogError(exception.Message, exception);
                _logger.LogError(e1.Message, e1);
            }

            return ValueTask.CompletedTask;
        }

        /// <summary>
        /// 通用事件
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="oneBotContext"></param>
        /// <param name="e"></param>
        /// <returns></returns>
        private ValueTask OnGeneralEvent<T>(object sender, OneBotContext<T> oneBotContext, BaseSoraEventArgs e)
            where T : BaseSoraEventArgs
        {
            Exception? exception = null;
            try
            {
                Event.Fire(oneBotContext, e);
            }
            catch (Exception e1)
            {
                exception = e1;
            }

            if (exception != null)
            {
                return EventOnException(sender, oneBotContext, exception);
            }

            return ValueTask.CompletedTask;
        }

        #endregion 事件处理

        #region 指令路由

        /// <summary>
        /// 私聊消息分发
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="oneBotContext"></param>
        /// <param name="e"></param>
        /// <returns></returns>
        private ValueTask EventOnPrivateMessage(object sender, OneBotContext<PrivateMessageEventArgs> oneBotContext,
            PrivateMessageEventArgs e)
        {
            Exception? exception = null;
            try
            {
                if (Event.FirePrivateMessageReceived(oneBotContext, e) != 0) return ValueTask.CompletedTask;
                if (_matchingRootNode.ProcessingCommandMapping(oneBotContext, sender, e,
                        new CommandLexer(e.Message.MessageBody)) != 0) return ValueTask.CompletedTask;
                Event.Fire(oneBotContext, e);
            }
            catch (Exception e1)
            {
                exception = e1;
            }

            if (exception != null)
            {
                return EventOnException(sender, oneBotContext, exception);
            }

            return ValueTask.CompletedTask;
        }

        /// <summary>
        /// 群聊消息分发
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="oneBotContext"></param>
        /// <param name="e"></param>
        /// <returns></returns>
        private ValueTask EventOnGroupMessage(object sender, OneBotContext<GroupMessageEventArgs> oneBotContext,
            GroupMessageEventArgs e)
        {
            Exception? exception = null;
            try
            {
                if (Event.FireGroupMessageReceived(oneBotContext, e) != 0) return ValueTask.CompletedTask;
                if (_matchingRootNode.ProcessingCommandMapping(oneBotContext, sender, e,
                        new CommandLexer(e.Message.MessageBody)) != 0) return ValueTask.CompletedTask;
                Event.Fire(oneBotContext, e);
            }
            catch (Exception e1)
            {
                exception = e1;
            }

            if (exception != null)
            {
                return EventOnException(sender, oneBotContext, exception);
            }

            return ValueTask.CompletedTask;
        }

        #endregion 指令路由

        #region 注册指令

        /// <summary>
        /// 注册指令
        /// </summary>
        public void RegisterCommand()
        {
            var onebotController = _serviceProvider.GetServices<IOneBotController>();
            foreach (var s in onebotController)
            {
                var clazz = s.GetType();
                var methods = clazz.GetMethods();
                foreach (var method in methods)
                {
                    var methodAttributes = method.CustomAttributes;
                    if (Attribute.IsDefined(method, typeof(CommandAttribute)))
                    {
                        var attr = Attribute.GetCustomAttribute(method, typeof(CommandAttribute)) as CommandAttribute;
#pragma warning disable 8604
                        RegisterCommand(s, method, attr);
#pragma warning restore 8604
                    }

                    if (Attribute.IsDefined(method, typeof(CQJsonAttribute)))
                    {
                        if (_jsonRouterService == null)
                        {
                            _logger.LogWarning($"检测到 CQ:Json 路由功能已被关闭，但依然有方法使用了 [CQJson]。{clazz.FullName}::{method.Name}");
                        }
                        else
                        {
                            var attr = Attribute.GetCustomAttribute(method, typeof(CQJsonAttribute)) as CQJsonAttribute;
#pragma warning disable 8604
                            _jsonRouterService.Register(s, method, attr);
                            _logger.LogDebug($"成功添加 CQ:Json ：{attr.AppId}\r\n{clazz.FullName}::{method.Name}");
#pragma warning restore 8604
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 将 [Command] 中的 pattern 和 alias 拆解为多个 pattern。
        /// </summary>
        /// <param name="commandObj"></param>
        /// <param name="commandMethod"></param>
        /// <param name="attribute"></param>
        private void RegisterCommand(IOneBotController commandObj, MethodInfo commandMethod, CommandAttribute attribute)
        {
            List<string> command = attribute.Pattern.Trim().Split(' ').ToList();
            List<string> aliasList = attribute.Alias.Select(s => s.Trim()).Where(s => s.Length > 0)
                .ToList();

            try
            {
                RegisterCommand(commandObj, commandMethod, command, attribute);
            }
            catch (ArgumentException e)
            {
                _logger.LogError(
                    $"{e.Message} : \n" +
                    $"{e.StackTrace}"
                );
            }

            foreach (var c in aliasList.Select(s => s.Split(' ').ToList()))
            {
                try
                {
                    RegisterCommand(commandObj, commandMethod, c, attribute);
                }
                catch (ArgumentException e)
                {
                    _logger.LogError(
                        $"{e.Message} : \n" +
                        $"{e.StackTrace}"
                    );
                }
            }
        }

        /// <summary>
        /// 将 pattern 拆解为多个参数。
        /// </summary>
        /// <param name="commandObj"></param>
        /// <param name="commandMethod"></param>
        /// <param name="matchPattern"></param>
        /// <param name="attribute"></param>
        /// <exception cref="ArgumentException"></exception>
        private void RegisterCommand(IOneBotController commandObj, MethodInfo commandMethod, List<string> matchPattern,
            CommandAttribute attribute)
        {
            // 参数类型
            List<Type> parametersType = new List<Type>();

            // 参数匹配类型：0全字匹配 1参数 2可选参数
            List<int> parametersMatchingType = new List<int>();

            // 被全字匹配的字符串或参数名
            List<string> parametersName = new List<string>();

            // 这个参数是否被用过了
            List<bool> parametersUsed = new List<bool>();

            // 这个参数对应函数的形参列表哪一位。负数表示不映射。
            List<int> parameterPositionMapping = new List<int>();

            // 从指令定义中解析出 参数匹配类型 和 参数匹配字符串或参数名
            var withOptional = false;
            foreach (var s in matchPattern)
            {
                parametersType.Add(typeof(string));
                parametersUsed.Add(false);
                parameterPositionMapping.Add(-1);

                // 必选参数
                if (s.StartsWith("<") && s.EndsWith(">"))
                {
                    if (withOptional)
                    {
                        throw new ArgumentException($"我觉得你的指令定义有问题。可选参数只能写在末尾哦。");
                    }

                    parametersMatchingType.Add(1);
                    var paraName = s.Substring(1, s.Length - 2);
                    for (var j = 0; j < parametersName.Count; j++)
                    {
                        if (parametersMatchingType[j] == 0) continue;
                        if (parametersName[j] == paraName)
                        {
                            throw new ArgumentException($"我觉得你的指令定义有问题。参数名必须互异。");
                        }
                    }

                    parametersName.Add(paraName);
                    continue;
                }

                // 可选参数
                if (s.StartsWith("[") && s.EndsWith("]"))
                {
                    withOptional = true;
                    parametersMatchingType.Add(2);
                    var paraName = s.Substring(1, s.Length - 2);
                    for (var j = 0; j < parametersName.Count; j++)
                    {
                        if (parametersMatchingType[j] == 0) continue;
                        if (parametersName[j] == paraName)
                        {
                            throw new ArgumentException($"我觉得你的指令定义有问题。参数名必须互异。");
                        }
                    }

                    parametersName.Add(paraName);
                    continue;
                }

                // 全字匹配
                if (withOptional)
                {
                    throw new ArgumentException($"我觉得你的指令定义有问题。可选参数只能写在末尾哦。");
                }

                parametersMatchingType.Add(0);
                parametersName.Add(s);
                continue;
            }

            ;

            // 先检查属性
            var functionParametersList = commandMethod.GetParameters();
            for (var i = 0; i < functionParametersList.Length; i++)
            {
                var type = functionParametersList[i];
                if (System.Attribute.IsDefined(type, typeof(CommandParameterAttribute)))
                {
                    var attr = System.Attribute.GetCustomAttribute(type, typeof(CommandParameterAttribute)) as CommandParameterAttribute;
#pragma warning disable 8602
                    var paraName = attr.Name;
#pragma warning restore 8602

                    var idx = -1;
                    for (var j = 0; j < parametersName.Count; j++)
                    {
                        if (parametersMatchingType[j] == 0) continue;
                        if (parametersName[j] != paraName) continue;

                        idx = j;
                        break;
                    }

                    if (idx < 0)
                    {
                        throw new ArgumentException($"我觉得你的指令定义有问题。在指令定义中找不到参数名为 {paraName} 的参数。");
                    }

                    parametersType[idx] = type.ParameterType;
                    parametersUsed[idx] = true;
                    parameterPositionMapping[idx] = i;
                }
            }


            // 再检查形参参数名
            for (var i = 0; i < parametersName.Count; i++)
            {
                if (parametersUsed[i]) continue;
                if (parametersMatchingType[i] == 0) continue;

                var paraName = parametersName[i];

                for (var j = 0; j < functionParametersList.Length; j++)
                {
                    var type = functionParametersList[j];
                    if (type.Name == paraName)
                    {
                        parametersType[i] = type.ParameterType;
                        parametersUsed[i] = true;
                        parameterPositionMapping[i] = j;
                    }
                }
            }

            _matchingRootNode.Register(
                new CommandModel(commandObj, commandMethod,
                    parametersType, parametersMatchingType, parametersName, parameterPositionMapping,
                    attribute)
                , 0);
            _logger.LogDebug($"成功添加指令：{string.Join(", ", matchPattern.ToArray())}\r\n{commandObj.GetType().FullName}::{commandMethod.Name}");
        }

        #endregion 注册指令
    }
}
