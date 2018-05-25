﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using Common;
using Common.Log;
using Lykke.Messaging.Contract;
using Lykke.Cqrs.InfrastructureCommands;
using Lykke.Cqrs.Utils;

namespace Lykke.Cqrs
{
    internal class CommandDispatcher : IDisposable
    {
        private readonly Dictionary<Type, (string, Func<object, Endpoint, string, CommandHandlingResult>)> m_handlers =
            new Dictionary<Type, (string, Func<object, Endpoint, string, CommandHandlingResult>)>();
        private readonly string m_BoundedContext;
        private readonly ILog _log;
        private readonly MethodInfo _getAwaiterInfo;
        private readonly bool _enableCommandsLogging;

        private long m_FailedCommandRetryDelay;

        public CommandDispatcher(
            ILog log,
            string boundedContext,
            long failedCommandRetryDelay = 60000)
            : this(log, boundedContext, true, failedCommandRetryDelay)
        {
        }

        public CommandDispatcher(
            ILog log,
            string boundedContext,
            bool enableCommandsLogging,
            long failedCommandRetryDelay = 60000)
        {
            _log = log;
            m_FailedCommandRetryDelay = failedCommandRetryDelay;
            m_BoundedContext = boundedContext;
            _enableCommandsLogging = enableCommandsLogging;

            var taskMethods = typeof(Task<CommandHandlingResult>).GetMethods(BindingFlags.Public | BindingFlags.Instance);
            var awaiterResultType = typeof(TaskAwaiter<CommandHandlingResult>);
            _getAwaiterInfo = taskMethods.First(m => m.Name == "GetAwaiter" && m.ReturnType == awaiterResultType);
        }

        public void Wire(object o, params OptionalParameterBase[] parameters)
        {
            if (o == null)
                throw new ArgumentNullException("o");

            parameters = parameters
                .Concat(new OptionalParameterBase[] { new OptionalParameter<string>("boundedContext", m_BoundedContext) })
                .ToArray();

            var handleMethods = o.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(m => m.Name == "Handle" &&
                    !m.IsGenericMethod &&
                    m.GetParameters().Length > 0 &&
                    !m.GetParameters().First().ParameterType.IsInterface)
                .Select(m => new
                {
                    method = m,
                    returnType = m.ReturnType,
                    commandType = m.GetParameters().First().ParameterType,
                    callParameters = m.GetParameters().Skip(1).Select(p => new
                    {
                        parameter = p,
                        optionalParameter = parameters.FirstOrDefault(par => par.Name == p.Name || par.Name == null && p.ParameterType == par.Type),
                    })
                })
                .Where(m => m.callParameters.All(p => p.parameter != null));

            foreach (var method in handleMethods)
            {
                RegisterHandler(
                    method.commandType,
                    o,
                    method.callParameters.ToDictionary(p => p.parameter, p => p.optionalParameter.Value),
                    method.returnType);
            }
        }

        private Expression InvokeFunc(object o)
        {
            return Expression.Call(Expression.Constant(o), o.GetType().GetMethod("Invoke"));
        }

        private bool IsFunc(Type type)
        {
            return (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Func<>));
        }

        private void RegisterHandler(
            Type commandType,
            object o,
            Dictionary<ParameterInfo, object> optionalParameters,
            Type returnType)
        {
            var isRoutedCommandHandler = commandType.IsGenericType && commandType.GetGenericTypeDefinition() == typeof (RoutedCommand<>);
            var command = Expression.Parameter(typeof(object), "command");
            var endpoint = Expression.Parameter(typeof(Endpoint), "endpoint");
            var route = Expression.Parameter(typeof(string), "route");

            Expression commandParameter;
            Type handledType;
            if (!isRoutedCommandHandler)
            {
                commandParameter = Expression.Convert(command, commandType);
                handledType = commandType;
            }
            else
            {
                handledType = commandType.GetGenericArguments()[0];
                var ctor = commandType.GetConstructor(new[] { handledType, typeof(Endpoint) ,typeof(string)});
                commandParameter = Expression.New(ctor, Expression.Convert(command, handledType), endpoint, route);
            }

            (string, Func<object, Endpoint, string, CommandHandlingResult>) handlerInfo;
            if (m_handlers.TryGetValue(handledType, out handlerInfo))
                throw new InvalidOperationException(
                    $"Only one handler per command is allowed. Command {commandType} handler is already registered in bound context {m_BoundedContext}. Can not register {o} as handler for it");

            // prepare variables expressions
            var variables = optionalParameters
                 .Where(p => p.Value != null && IsFunc(p.Value.GetType()))
                 .ToDictionary(p => p.Key.Name, p => Expression.Variable(p.Key.ParameterType, p.Key.Name));

            //prepare parameters expression to make handle call
            var parameters = new[] { commandParameter }
                 .Concat(optionalParameters.Select(p => 
                     variables.ContainsKey(p.Key.Name)
                     ? (Expression)variables[p.Key.Name]
                     : (Expression)Expression.Constant(p.Value, p.Key.ParameterType))).ToArray();

            var handleCall = Expression.Call(Expression.Constant(o), "Handle", null, parameters);
            MethodCallExpression resultCall;
            if (returnType == typeof(Task<CommandHandlingResult>))
            {
                var awaiterCall = Expression.Call(handleCall, _getAwaiterInfo);
                resultCall = Expression.Call(awaiterCall, "GetResult", null);
            }
            else if (returnType == typeof(Task))
            {
                var awaiterCall = Expression.Call(handleCall, "GetAwaiter", null);
                resultCall = Expression.Call(awaiterCall, "GetResult", null);
            }
            else
            {
                resultCall = handleCall;
            }

            var disposableType = typeof (IDisposable);
            var call = Expression.Block(
                 variables.Values.AsEnumerable(), //declare variables to populate from func factoreis
                 variables
                     .Select(p => Expression.Assign(p.Value, InvokeFunc(optionalParameters.Single(x => x.Key.Name == p.Key).Value))) // invoke func and assign result to variable
                     .Cast<Expression>()
                     .Concat(new[]
                         {
                            Expression.TryFinally(
                                resultCall,
                                Expression.Block(variables
                                    .Select(v =>
                                        Expression.IfThen( //dispose variable if disposable and not null
                                            Expression.And(Expression.NotEqual(v.Value, Expression.Constant(null)), Expression.TypeIs(v.Value, disposableType)),
                                            Expression.Call(Expression.Convert(v.Value, disposableType) , disposableType.GetMethod("Dispose"))))
                                    .Cast<Expression>()
                                    .DefaultIfEmpty(Expression.Empty())))
                         }));

            if (returnType != typeof(Task<CommandHandlingResult>) && returnType != typeof(CommandHandlingResult))
            {
                var returnLabel = Expression.Label(
                    Expression.Label(typeof(CommandHandlingResult)),
                    Expression.Constant(new CommandHandlingResult { Retry = false, RetryDelay = 0 }));
                call = Expression.Block(call, returnLabel);
            }
            var lambda = (Expression<Func<object, Endpoint, string, CommandHandlingResult>>)Expression.Lambda(call, command, endpoint, route);

            m_handlers.Add(handledType, (o.GetType().Name, lambda.Compile()));
        }

        public void Dispatch(
            object command,
            AcknowledgeDelegate acknowledge,
            Endpoint commandOriginEndpoint,
            string route)
        {
            if (!m_handlers.TryGetValue(command.GetType(), out var handlerInfo))
            {
                _log.WriteWarningAsync(
                    nameof(CommandDispatcher),
                    nameof(Dispatch),
                    $"Failed to handle command {command} in bound context {m_BoundedContext}, no handler was registered for it");
                acknowledge(m_FailedCommandRetryDelay, false);
                return;
            }

            Handle(
                command,
                acknowledge,
                handlerInfo.Item1,
                handlerInfo.Item2,
                commandOriginEndpoint,
                route);
        }

        private void Handle(
            object command,
            AcknowledgeDelegate acknowledge,
            string handlerTypeName,
            Func<object, Endpoint, string, CommandHandlingResult> handler,
            Endpoint commandOriginEndpoint,
            string route)
        {
            string commandType = command.GetType().Name;
            if (_enableCommandsLogging)
                _log.WriteInfoAsync(handlerTypeName, commandType, command?.ToJson() ?? "")
                    .GetAwaiter().GetResult();

            var telemtryOperation = TelemetryHelper.InitTelemetryOperation(
                "Cqrs handle command",
                handlerTypeName,
                commandType,
                m_BoundedContext);
            try
            {
                var result = handler(command, commandOriginEndpoint, route);
                acknowledge(result.RetryDelay, !result.Retry);
            }
            catch (Exception e)
            {
                _log.WriteErrorAsync(handlerTypeName, commandType, command?.ToJson() ?? "", e)
                    .GetAwaiter().GetResult();

                acknowledge(m_FailedCommandRetryDelay, false);

                TelemetryHelper.SubmitException(telemtryOperation, e);
            }
            finally
            {
                TelemetryHelper.SubmitOperationResult(telemtryOperation);
            }
        }

        public void Dispose()
        {
        }
    }
}