﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EasyDAL.Exchange.AdoNet;
using EasyDAL.Exchange.DataBase;
using EasyDAL.Exchange.DynamicParameter;
using EasyDAL.Exchange.MapperX;
using EasyDAL.Exchange.Reader;

namespace EasyDAL.Exchange.AdoNet
{
    internal static partial class SqlMapper
    {

        /// <summary>
        /// 查询单行
        /// </summary>
        private static async Task<T> QueryRowAsync<T>(this IDbConnection cnn, Row row, Type effectiveType, CommandDefinition command)
        {
            object param = command.Parameters;
            var identity = new Identity(command.CommandText, command.CommandType, cnn, effectiveType, param?.GetType(), null);
            var info = GetCacheInfo(identity, param, command.AddToCache);
            bool wasClosed = cnn.State == ConnectionState.Closed;
            //var cancel = command.CancellationToken;
            using (var cmd = command.TrySetupAsyncCommand(cnn, info.ParamReader))
            {
                DbDataReader reader = null;
                try
                {
                    if (wasClosed)
                    {
                        await cnn.TryOpenAsync(default(CancellationToken)).ConfigureAwait(false);
                    }
                    reader = await ExecuteReaderWithFlagsFallbackAsync(cmd, wasClosed, (row & Row.Single) != 0
                    ? CommandBehavior.SequentialAccess | CommandBehavior.SingleResult // need to allow multiple rows, to check fail condition
                    : CommandBehavior.SequentialAccess | CommandBehavior.SingleResult | CommandBehavior.SingleRow, default(CancellationToken)).ConfigureAwait(false);

                    T result = default(T);
                    if (await reader.ReadAsync(default(CancellationToken)).ConfigureAwait(false) && reader.FieldCount != 0)
                    {
                        var tuple = info.Deserializer;
                        int hash = GetColumnHash(reader);
                        if (tuple.Func == null || tuple.Hash != hash)
                        {
                            tuple = info.Deserializer = new DeserializerState(hash, GetDeserializer(effectiveType, reader, 0, -1, false));
                            if (command.AddToCache) SetQueryCache(identity, info);
                        }

                        var func = tuple.Func;

                        object val = func(reader);
                        if (val == null || val is T)
                        {
                            result = (T)val;
                        }
                        else
                        {
                            var convertToType = Nullable.GetUnderlyingType(effectiveType) ?? effectiveType;
                            result = (T)Convert.ChangeType(val, convertToType, CultureInfo.InvariantCulture);
                        }
                        if ((row & Row.Single) != 0 && await reader.ReadAsync(default(CancellationToken)).ConfigureAwait(false))
                        {
                            ThrowMultipleRows(row);
                        }
                        while (await reader.ReadAsync(default(CancellationToken)).ConfigureAwait(false))
                        { /* ignore rows after the first */ }
                    }
                    else if ((row & Row.FirstOrDefault) == 0) // demanding a row, and don't have one
                    {
                        ThrowZeroRows(row);
                    }
                    while (await reader.NextResultAsync(default(CancellationToken)).ConfigureAwait(false))
                    { /* ignore result sets after the first */ }
                    return result;
                }
                finally
                {
                    using (reader) { /* dispose if non-null */ }
                    if (wasClosed) cnn.Close();
                }
            }
        }
        
        private static async Task<IEnumerable<T>> QueryAsync<T>(this IDbConnection cnn, Type effectiveType, CommandDefinition command)
        {
            object param = command.Parameters;
            var identity = new Identity(command.CommandText, command.CommandType, cnn, effectiveType, param?.GetType(), null);
            var info = GetCacheInfo(identity, param, command.AddToCache);
            bool wasClosed = cnn.State == ConnectionState.Closed;
            //var cancel = command.CancellationToken;
            using (var cmd = command.TrySetupAsyncCommand(cnn, info.ParamReader))
            {
                DbDataReader reader = null;
                try
                {
                    if (wasClosed)
                    {
                        await cnn.TryOpenAsync(default(CancellationToken)).ConfigureAwait(false);
                    }
                    reader = await ExecuteReaderWithFlagsFallbackAsync(cmd, wasClosed, CommandBehavior.SequentialAccess | CommandBehavior.SingleResult, default(CancellationToken)).ConfigureAwait(false);

                    var tuple = info.Deserializer;
                    int hash = GetColumnHash(reader);
                    if (tuple.Func == null || tuple.Hash != hash)
                    {
                        if (reader.FieldCount == 0)
                            return Enumerable.Empty<T>();
                        tuple = info.Deserializer = new DeserializerState(hash, GetDeserializer(effectiveType, reader, 0, -1, false));
                        if (command.AddToCache) SetQueryCache(identity, info);
                    }

                    var func = tuple.Func;

                    if (command.Buffered)
                    {
                        var buffer = new List<T>();
                        var convertToType = Nullable.GetUnderlyingType(effectiveType) ?? effectiveType;
                        while (await reader.ReadAsync(default(CancellationToken)).ConfigureAwait(false))
                        {
                            object val = func(reader);
                            if (val == null || val is T)
                            {
                                buffer.Add((T)val);
                            }
                            else
                            {
                                buffer.Add((T)Convert.ChangeType(val, convertToType, CultureInfo.InvariantCulture));
                            }
                        }
                        while (await reader.NextResultAsync(default(CancellationToken)).ConfigureAwait(false))
                        { /* ignore subsequent result sets */ }
                        command.OnCompleted();
                        return buffer;
                    }
                    else
                    {
                        // can't use ReadAsync / cancellation; but this will have to do
                        wasClosed = false; // don't close if handing back an open reader; rely on the command-behavior
                        var deferred = ExecuteReaderSync<T>(reader, func, command.Parameters);
                        reader = null; // to prevent it being disposed before the caller gets to see it
                        return deferred;
                    }
                }
                finally
                {
                    using (reader) { /* dispose if non-null */ }
                    if (wasClosed) cnn.Close();
                }
            }
        }
        
        /// <summary>
        /// Execute a command asynchronously using .NET 4.5 Task.
        /// </summary>
        /// <param name="cnn">The connection to execute on.</param>
        /// <param name="command">The command to execute on this connection.</param>
        /// <returns>The number of rows affected.</returns>
        private static Task<int> ExecuteAsync(this IDbConnection cnn, CommandDefinition command)
        {
            object param = command.Parameters;
            IEnumerable multiExec = GetMultiExec(param);
            if (multiExec != null)
            {
                return ExecuteMultiImplAsync(cnn, command, multiExec);
            }
            else
            {
                return ExecuteImplAsync(cnn, command, param);
            }
        }

        /*
         * ExecuteImpl
         */
        private static async Task<int> ExecuteImplAsync(IDbConnection cnn, CommandDefinition command, object param)
        {
            var identity = new Identity(command.CommandText, command.CommandType, cnn, null, param?.GetType(), null);
            var info = GetCacheInfo(identity, param, command.AddToCache);
            bool wasClosed = cnn.State == ConnectionState.Closed;
            using (var cmd = command.TrySetupAsyncCommand(cnn, info.ParamReader))
            {
                try
                {
                    if (wasClosed)
                    {
                        await cnn.TryOpenAsync(default(CancellationToken)).ConfigureAwait(false);
                    }
                    var result = await cmd.ExecuteNonQueryAsync(default(CancellationToken)).ConfigureAwait(false);
                    command.OnCompleted();
                    return result;
                }
                finally
                {
                    if (wasClosed) cnn.Close();
                }
            }
        }
        
        private static async Task<T> ExecuteScalarImplAsync<T>(IDbConnection cnn, CommandDefinition command)
        {
            Action<IDbCommand, object> paramReader = null;
            object param = command.Parameters;
            if (param != null)
            {
                var identity = new Identity(command.CommandText, command.CommandType, cnn, null, param.GetType(), null);
                paramReader = GetCacheInfo(identity, command.Parameters, command.AddToCache).ParamReader;
            }

            DbCommand cmd = null;
            bool wasClosed = cnn.State == ConnectionState.Closed;
            object result;
            try
            {
                cmd = command.TrySetupAsyncCommand(cnn, paramReader);
                if (wasClosed)
                {
                    await cnn.TryOpenAsync(default(CancellationToken)).ConfigureAwait(false);
                }
                result = await cmd.ExecuteScalarAsync(default(CancellationToken)).ConfigureAwait(false);
                command.OnCompleted();
            }
            finally
            {
                if (wasClosed)
                {
                    cnn.Close();
                }
                cmd?.Dispose();
            }
            return Parse<T>(result);
        }


        private static async Task<int> ExecuteMultiImplAsync(IDbConnection cnn, CommandDefinition command, IEnumerable multiExec)
        {
            bool isFirst = true;
            int total = 0;
            bool wasClosed = cnn.State == ConnectionState.Closed;
            try
            {
                if (wasClosed)
                {
                    await cnn.TryOpenAsync(default(CancellationToken)).ConfigureAwait(false);
                }

                CacheInfo info = null;
                string masterSql = null;
                if ((command.Flags & CommandFlags.Pipelined) != 0)
                {
                    const int MAX_PENDING = 100;
                    var pending = new Queue<AsyncExecState>(MAX_PENDING);
                    DbCommand cmd = null;
                    try
                    {
                        foreach (var obj in multiExec)
                        {
                            if (isFirst)
                            {
                                isFirst = false;
                                cmd = command.TrySetupAsyncCommand(cnn, null);
                                masterSql = cmd.CommandText;
                                var identity = new Identity(command.CommandText, cmd.CommandType, cnn, null, obj.GetType(), null);
                                info = GetCacheInfo(identity, obj, command.AddToCache);
                            }
                            else if (pending.Count >= MAX_PENDING)
                            {
                                var recycled = pending.Dequeue();
                                total += await recycled.Task.ConfigureAwait(false);
                                cmd = recycled.Command;
                                cmd.CommandText = masterSql; // because we do magic replaces on "in" etc
                                cmd.Parameters.Clear(); // current code is Add-tastic
                            }
                            else
                            {
                                cmd = command.TrySetupAsyncCommand(cnn, null);
                            }
                            info.ParamReader(cmd, obj);

                            var task = cmd.ExecuteNonQueryAsync(default(CancellationToken));
                            pending.Enqueue(new AsyncExecState(cmd, task));
                            cmd = null; // note the using in the finally: this avoids a double-dispose
                        }
                        while (pending.Count != 0)
                        {
                            var pair = pending.Dequeue();
                            using (pair.Command) { /* dispose commands */ }
                            total += await pair.Task.ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        // this only has interesting work to do if there are failures
                        using (cmd) { /* dispose commands */ }
                        while (pending.Count != 0)
                        { // dispose tasks even in failure
                            using (pending.Dequeue().Command) { /* dispose commands */ }
                        }
                    }
                }
                else
                {
                    using (var cmd = command.TrySetupAsyncCommand(cnn, null))
                    {
                        foreach (var obj in multiExec)
                        {
                            if (isFirst)
                            {
                                masterSql = cmd.CommandText;
                                isFirst = false;
                                var identity = new Identity(command.CommandText, cmd.CommandType, cnn, null, obj.GetType(), null);
                                info = GetCacheInfo(identity, obj, command.AddToCache);
                            }
                            else
                            {
                                cmd.CommandText = masterSql; // because we do magic replaces on "in" etc
                                cmd.Parameters.Clear(); // current code is Add-tastic
                            }
                            info.ParamReader(cmd, obj);
                            total += await cmd.ExecuteNonQueryAsync(default(CancellationToken)).ConfigureAwait(false);
                        }
                    }
                }

                command.OnCompleted();
            }
            finally
            {
                if (wasClosed) cnn.Close();
            }
            return total;
        }

        private static IEnumerable<T> ExecuteReaderSync<T>(IDataReader reader, Func<IDataReader, object> func, object parameters)
        {
            using (reader)
            {
                while (reader.Read())
                {
                    yield return (T)func(reader);
                }
                while (reader.NextResult()) { /* ignore subsequent result sets */ }
                (parameters as IParameterCallbacks)?.OnCompleted();
            }
        }

        /*
         * Common
         */
        /// <summary>
        /// Attempts setup a <see cref="DbCommand"/> on a <see cref="DbConnection"/>, with a better error message for unsupported usages.
        /// </summary>
        private static DbCommand TrySetupAsyncCommand(this CommandDefinition command, IDbConnection cnn, Action<IDbCommand, object> paramReader)
        {
            if (command.SetupCommand(cnn, paramReader) is DbCommand dbCommand)
            {
                return dbCommand;
            }
            else
            {
                throw new InvalidOperationException("Async operations require use of a DbConnection or an IDbConnection where .CreateCommand() returns a DbCommand");
            }
        }
        /// <summary>
        /// Attempts to open a connection asynchronously, with a better error message for unsupported usages.
        /// </summary>
        private static Task TryOpenAsync(this IDbConnection cnn, CancellationToken cancel)
        {
            if (cnn is DbConnection dbConn)
            {
                return dbConn.OpenAsync(cancel);
            }
            else
            {
                throw new InvalidOperationException("Async operations require use of a DbConnection or an already-open IDbConnection");
            }
        }
        private static Task<DbDataReader> ExecuteReaderWithFlagsFallbackAsync(DbCommand cmd, bool wasClosed, CommandBehavior behavior, CancellationToken cancellationToken)
        {
            var task = cmd.ExecuteReaderAsync(GetBehavior(wasClosed, behavior), cancellationToken);
            if (task.Status == TaskStatus.Faulted && Settings.DisableCommandBehaviorOptimizations(behavior, task.Exception.InnerException))
            { // we can retry; this time it will have different flags
                return cmd.ExecuteReaderAsync(GetBehavior(wasClosed, behavior), cancellationToken);
            }
            return task;
        }

    }
}
