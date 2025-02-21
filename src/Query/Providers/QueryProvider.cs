﻿using Kros.Caching;
using Kros.Data;
using Kros.Data.BulkActions;
using Kros.Data.Schema;
using Kros.KORM.Data;
using Kros.KORM.Helper;
using Kros.KORM.Materializer;
using Kros.KORM.Metadata;
using Kros.KORM.Properties;
using Kros.KORM.Query.Providers;
using Kros.KORM.Query.Sql;
using Kros.Utils;
using Microsoft.Data.SqlClient;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Kros.KORM.Query
{
    /// <summary>
    /// Provider, which know execute query.
    /// </summary>
    /// <seealso cref="Kros.KORM.Query.IQueryProvider" />
    public abstract class QueryProvider : IQueryProvider
    {
        #region Nested types

        private class DbCommandInfo : IDisposable
        {
            public DbCommandInfo(DbCommand command, IDataReaderEnvelope reader)
            {
                Command = command;
                Reader = reader;
            }

            public DbCommand Command { get; }
            public IDataReaderEnvelope Reader { get; }

            public void Dispose() => Command?.Dispose();
        }

        private sealed class IdGeneratorHelper : IIdGenerator
        {
            private readonly IDbConnection _connection;
            private readonly IIdGenerator _idGenerator;

            public IdGeneratorHelper(IIdGenerator idGenerator, IDbConnection connection)
            {
                _idGenerator = idGenerator;
                _connection = connection;
            }

            public object GetNext() => _idGenerator.GetNext();

            public void InitDatabaseForIdGenerator() => _idGenerator.InitDatabaseForIdGenerator();

            private bool _disposed = false;

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }
                _idGenerator.Dispose();
                _connection.Dispose();
                _disposed = true;
            }
        }

        #endregion

        #region Constants

        private const string RETURN_VALUE_PARAM_NAME = "returnValue";

        #endregion

        #region Private fields

        private readonly ILogger _logger;
        private readonly IDatabaseMapper _databaseMapper;
        private readonly IModelBuilder _modelBuilder;
        private readonly KormConnectionSettings _connectionSettings = null;
        private DbConnection _connection = null;
        private readonly Cache<string, TableSchema> _tableSchemas =
            new Cache<string, TableSchema>(StringComparer.OrdinalIgnoreCase);
        private MethodInfo _nonGenericMaterializeMethod = null;
        private readonly Lazy<TransactionHelper> _transactionHelper;
        private Lazy<ISqlExpressionVisitor> _sqlExpressionVisitor;

        #endregion

        #region Constructors

        /// <summary>
        /// Initializes a new instance of the <see cref="QueryProvider" /> class.
        /// </summary>
        /// <param name="connectionSettings">The connection string settings.</param>
        /// <param name="sqlGeneratorFactory">The SQL generator factory.</param>
        /// <param name="modelBuilder">The model builder.</param>
        /// <param name="logger">The logger.</param>
        /// <param name="databaseMapper">The Database mapper.</param>
        protected QueryProvider(
            KormConnectionSettings connectionSettings,
            ISqlExpressionVisitorFactory sqlGeneratorFactory,
            IModelBuilder modelBuilder,
            ILogger logger,
            IDatabaseMapper databaseMapper)
        {
            _logger = Check.NotNull(logger, nameof(logger));
            _databaseMapper = Check.NotNull(databaseMapper, nameof(databaseMapper));
            _connectionSettings = Check.NotNull(connectionSettings, nameof(connectionSettings));

            InitSqlExpressionVisitor(Check.NotNull(sqlGeneratorFactory, nameof(sqlGeneratorFactory)));
            IsExternalConnection = false;
            _modelBuilder = Check.NotNull(modelBuilder, nameof(modelBuilder));
            _transactionHelper = new Lazy<TransactionHelper>(() => new TransactionHelper(Connection));
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="QueryProvider" /> class.
        /// </summary>
        /// <param name="externalConnection">The connection.</param>
        /// <param name="sqlGeneratorFactory">The SQL generator factory.</param>
        /// <param name="modelBuilder">The model builder.</param>
        /// <param name="logger">The logger.</param>
        /// <param name="databaseMapper">The Database mapper.</param>
        protected QueryProvider(
            DbConnection externalConnection,
            ISqlExpressionVisitorFactory sqlGeneratorFactory,
            IModelBuilder modelBuilder,
            ILogger logger,
            IDatabaseMapper databaseMapper)
        {
            _connection = Check.NotNull(externalConnection, nameof(externalConnection));
            _modelBuilder = Check.NotNull(modelBuilder, nameof(modelBuilder));
            _logger = Check.NotNull(logger, nameof(logger));
            _databaseMapper = Check.NotNull(databaseMapper, nameof(databaseMapper));

            InitSqlExpressionVisitor(Check.NotNull(sqlGeneratorFactory, nameof(sqlGeneratorFactory)));
            IsExternalConnection = true;
            _transactionHelper = new Lazy<TransactionHelper>(() => new TransactionHelper(Connection));
        }

        private void InitSqlExpressionVisitor(ISqlExpressionVisitorFactory sqlGeneratorFactory)
            => _sqlExpressionVisitor = new Lazy<ISqlExpressionVisitor>(() => sqlGeneratorFactory.CreateVisitor(Connection));

        #endregion

        #region IQueryProvider

        /// <summary>
        /// Returns <see cref="DbProviderFactory"/> for current provider.
        /// </summary>
        public abstract DbProviderFactory DbProviderFactory { get; }

        /// <summary>
        /// Returns, if provider supports preparing of command (<see cref="DbCommand.Prepare"/>).
        /// </summary>
        /// <returns>Returns <see langword="true"/>.</returns>
        public virtual bool SupportsPrepareCommand() => true;

        /// <summary>
        /// Returns, if provider support inserting into table, where primary key is set as Identity.
        /// </summary>
        /// <returns>Returns <see langword="true"/>.</returns>
        public virtual bool SupportsIdentity() => true;

        /// <inheritdoc cref="IQueryProvider.SetParameterDbType(DbParameter, string, string)"/>
        public void SetParameterDbType(DbParameter parameter, string tableName, string columnName)
        {
            TableSchema table = _tableSchemas.Get(tableName, () => LoadTableSchema(tableName));
            if (!table.Columns.Contains(columnName))
            {
                throw new InvalidOperationException(
                    string.Format(Resources.QueryProviderCouldNotGetDataTypeForColumn, tableName, columnName));
            }
            table.Columns[columnName].SetupParameter(parameter);
            parameter.Size = table.Columns[columnName].Size;
        }

        private TableSchema LoadTableSchema(string tableName)
        {
            IDatabaseSchemaLoader schemaLoader = GetSchemaLoader();
            TableSchema tableSchema = schemaLoader.LoadTableSchema(Connection, tableName);
            return tableSchema
                ?? throw new InvalidOperationException(string.Format(Resources.QueryProviderCouldNotGetTableSchema, tableName));
        }

        /// <summary>
        /// Creates <see cref="IDatabaseSchemaLoader"/> for specific database.
        /// </summary>
        protected abstract IDatabaseSchemaLoader GetSchemaLoader();

        /// <summary>
        /// Executes the specified query.
        /// </summary>
        /// <typeparam name="T">Type of model result.</typeparam>
        /// <param name="query">The query.</param>
        /// <returns>
        /// IEnumerable of models, which was materialized by query
        /// </returns>
        /// <exception cref="ArgumentNullException">If query is null.</exception>
        public virtual IEnumerable<T> Execute<T>(IQuery<T> query)
        {
            Check.NotNull(query, nameof(query));

            DbCommandInfo commandInfo = CreateCommand(query);
            Data.ConnectionHelper cnHelper = OpenConnection();
            _logger.LogCommand(commandInfo.Command);
            IDataReader reader = new ModelBuilder.QueryDataReader(commandInfo.Command, commandInfo.Reader,
                cnHelper.CloseConnection);
            return _modelBuilder.Materialize<T>(reader);
        }

        /// <summary>
        /// Executes the query, and returns the first column of the first row in the result set returned by the query.
        /// Additional columns or rows are ignored.
        /// </summary>
        /// <typeparam name="T">Type of model.</typeparam>
        /// <param name="query">The query.</param>
        /// <returns>
        /// The first column of the first row in the result set, or <see langword="null"/> if the result set is empty.
        /// Returns a maximum of 2033 characters.
        /// </returns>
        /// <exception cref="ArgumentNullException">If query is null.</exception>
        public object ExecuteScalar<T>(IQuery<T> query)
        {
            Check.NotNull(query, nameof(query));

            using (var cnHelper = OpenConnection())
            using (DbCommandInfo commandInfo = CreateCommand(query))
            {
                _logger.LogCommand(commandInfo.Command);
                if (commandInfo.Reader == null)
                {
                    return commandInfo.Command.ExecuteScalar();
                }
                else
                {
                    using (IDataReaderEnvelope reader = commandInfo.Reader)
                    {
                        reader.SetInnerReader(commandInfo.Command.ExecuteReader());
                        if (reader.Read())
                        {
                            return reader.GetValue(0);
                        }
                    }
                }
                return null;
            }
        }

        /// <inheritdoc/>
        public object ExecuteScalarCommand(IDbCommand command)
        {
            Check.NotNull(command, nameof(command));
            _logger.LogCommand(command);

            return command.ExecuteScalar();
        }

        /// <inheritdoc/>
        public async Task<object> ExecuteScalarCommandAsync(DbCommand command, CancellationToken cancellationToken = default)
        {
            Check.NotNull(command, nameof(command));
            _logger.LogCommand(command);

            return await command.ExecuteScalarAsync(cancellationToken);
        }

        /// <inheritdoc/>
        public async Task ExecuteInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default)
        {
            using (OpenConnection())
            using (var transaction = _transactionHelper.Value.BeginTransaction())
            {
                try
                {
                    await action(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                }
                catch
                {
                    await transaction.RollbackAsync(cancellationToken);
                    throw;
                }
            }
        }

        /// <summary>
        /// Executes the command.
        /// </summary>
        /// <param name="command">The command.</param>
        /// <exception cref="System.NotImplementedException"></exception>
        public int ExecuteNonQueryCommand(IDbCommand command)
        {
            Check.NotNull(command, nameof(command));
            _logger.LogCommand(command);

            return command.ExecuteNonQuery();
        }

        /// <inheritdoc/>
        public async Task<int> ExecuteNonQueryCommandAsync(DbCommand command, CancellationToken cancellationToken = default)
        {
            Check.NotNull(command, nameof(command));
            _logger.LogCommand(command);

            return await command.ExecuteNonQueryAsync(cancellationToken);
        }

        /// <inheritdoc/>
        public int ExecuteNonQuery(string query) => ExecuteNonQuery(query, null);

        /// <inheritdoc/>
        public int ExecuteNonQuery(string query, CommandParameterCollection parameters)
        {
            CheckCommandParameters(parameters);

            using (OpenConnection())
            using (DbCommand command = CreateCommand(query, parameters))
            {
                return ExecuteNonQueryCommand(command);
            }
        }

        /// <inheritdoc/>
        public async Task<int> ExecuteNonQueryAsync(string query, CancellationToken cancellationToken = default)
            => await ExecuteNonQueryAsync(query, (CommandParameterCollection)null, cancellationToken);

        /// <inheritdoc/>
        public Task<int> ExecuteNonQueryAsync(
            string query,
            CancellationToken cancellationToken = default,
            params object[] paramValues)
        {
            var paramsCollection = new CommandParameterCollection();
            var tempParameters = new ParamEnumerator(query);

            foreach (var parameter in paramValues)
            {
                if (tempParameters.MoveNext())
                {
                    paramsCollection.Add(tempParameters.Current, parameter);
                }
                else
                {
                    throw new ArgumentException(Resources.MoreParametersThanValues);
                }
            }

            if (tempParameters.MoveNext())
            {
                throw new ArgumentException(Resources.MoreValuesThanParameters);
            }

            return ExecuteNonQueryAsync(query, paramsCollection, cancellationToken);
        }

        /// <inheritdoc/>
        public async Task<int> ExecuteNonQueryAsync(
            string query,
            CommandParameterCollection parameters,
            CancellationToken cancellationToken = default)
        {
            CheckCommandParameters(parameters);

            using (OpenConnection())
            using (DbCommand command = CreateCommand(query, parameters))
            {
                return await ExecuteNonQueryCommandAsync(command, cancellationToken);
            }
        }

        /// <inheritdoc cref="IQueryProvider.ExecuteStoredProcedure{TResult}(string)"/>
        public TResult ExecuteStoredProcedure<TResult>(string storedProcedureName)
            => ExecuteStoredProcedure<TResult>(storedProcedureName, null);

        /// <inheritdoc cref="IQueryProvider.ExecuteStoredProcedure{TResult}(string, CommandParameterCollection)"/>
        public TResult ExecuteStoredProcedure<TResult>(string storedProcedureName, CommandParameterCollection parameters)
        {
            CheckCommandParameters(parameters);

            TResult result = default(TResult);

            Data.ConnectionHelper cnHelper = null;
            DbCommand command = null;
            bool callDispose = true;

            try
            {
                cnHelper = OpenConnection();
                command = CreateCommand(storedProcedureName, parameters);
                command.CommandType = CommandType.StoredProcedure;
                _logger.LogCommand(command);

                DbParameter returnParameter = GetOrAddCommandReturnParameter(command);

                if (typeof(IEnumerable).IsAssignableFrom(typeof(TResult)))
                {
                    IDataReader reader = new ModelBuilder.QueryDataReader(command, null, cnHelper.CloseConnection);
                    result = MaterializeStoredProcedureResult<TResult>(reader);
                    callDispose = false;
                }
                else
                {
                    result = MaterializeStoredProcedureResult<TResult>(command, returnParameter);
                }
                FillOutputParameters(command, parameters);
            }
            catch
            {
                throw;
            }
            finally
            {
                if (callDispose)
                {
                    command?.Dispose();
                    cnHelper?.Dispose();
                }
            }

            return result;
        }

        private TResult MaterializeStoredProcedureResult<TResult>(DbCommand command, DbParameter returnParameter)
        {
            TResult result = default(TResult);

            using (var reader = command.ExecuteReader())
            {
                if (reader.HasRows)
                {
                    if (typeof(TResult).IsClass && (typeof(TResult) != typeof(string)))
                    {
                        result = _modelBuilder.Materialize<TResult>(reader).FirstOrDefault();
                    }
                    else
                    {
                        reader.Read();
                        result = (TResult)reader.GetValue(0);
                    }
                }
                else if (returnParameter != null)
                {
                    result = (TResult)returnParameter.Value;
                }
            }

            return result;
        }

        private void FillOutputParameters(DbCommand sourceCommand, CommandParameterCollection parameters)
        {
            if (parameters != null)
            {
                foreach (CommandParameter param in parameters)
                {
                    if ((param.Direction == ParameterDirection.Output) || (param.Direction == ParameterDirection.InputOutput))
                    {
                        param.Value = sourceCommand.Parameters[param.ParameterName].Value;
                    }
                }
            }
        }

        private TResult MaterializeStoredProcedureResult<TResult>(IDataReader reader)
        {
            Type tresult = typeof(TResult);
            if (!tresult.IsGenericType)
            {
                throw new InvalidOperationException(Resources.ResultMustBeGenericIEnumerable);
            }
            if (_nonGenericMaterializeMethod == null)
            {
                _nonGenericMaterializeMethod = _modelBuilder.GetType().GetMethod(
                    nameof(IModelBuilder.Materialize), new Type[] { typeof(IDataReader) });
                if (_nonGenericMaterializeMethod == null)
                {
                    string modelBuilderType = _modelBuilder.GetType().FullName;
                    const string methodName = nameof(IModelBuilder.Materialize);
                    const string methodArg = nameof(IDataReader);
                    throw new InvalidOperationException(
                        string.Format(Resources.MissingMethodInModelBuilder, modelBuilderType, methodName, methodArg));
                }
            }
            MethodInfo materializeMethod = _nonGenericMaterializeMethod.MakeGenericMethod(tresult.GenericTypeArguments[0]);

            return (TResult)materializeMethod.Invoke(_modelBuilder, new object[] { reader });
        }

        /// <summary>
        /// Creates instance of <see cref="IBulkInsert"/>.
        /// </summary>
        /// <returns>Instance of <see cref="IBulkInsert"/>.</returns>
        public abstract IBulkInsert CreateBulkInsert(object options);

        /// <summary>
        /// Creates instance of <see cref="IBulkUpdate"/>.
        /// </summary>
        /// <returns>Instance of <see cref="IBulkUpdate"/>.</returns>
        public abstract IBulkUpdate CreateBulkUpdate();

        /// <summary>
        /// Vytvorí inicializovaný príkaz <see cref="DbCommand"/>, pre aktuálnu transakciu.
        /// Používa sa iba v rámci volania <see cref="ExecuteInTransactionAsync(Func{Task})"/>.
        /// </summary>
        /// <returns>Inicializovaný príkaz.</returns>
        public DbCommand GetCommandForCurrentTransaction() => _transactionHelper.Value.CreateCommand();

        /// <inheritdoc/>
        public ITransaction BeginTransaction(IsolationLevel isolationLevel) =>
            _transactionHelper.Value.BeginTransaction(isolationLevel);

        /// <inheritdoc/>
        [Obsolete("Method is deprecated. Use CreateIdGenerator(Type, string, int).")]
        public IIdGenerator CreateIdGenerator(string tableName, int batchSize)
            => CreateIdGenerator(typeof(int), tableName, batchSize);

        /// <inheritdoc/>
        public IIdGenerator CreateIdGenerator(Type dataType, string tableName, int batchSize)
        {
            var connection = GetConnectionForIdGenerator();
            var factory = IdGeneratorFactories.GetFactory(dataType, connection);
            return new IdGeneratorHelper(factory.GetGenerator(tableName, batchSize), connection);
        }

        /// <inheritdoc/>
        public IIdGeneratorsForDatabaseInit GetIdGeneratorsForDatabaseInit()
            => IdGeneratorFactories.GetGeneratorsForDatabaseInit(GetConnectionForIdGenerator());

        private IDbConnection GetConnectionForIdGenerator()
        {
            var connection = (Connection as ICloneable).Clone() as DbConnection;
            try
            {
                connection.Open();
            }
            catch (SqlException ex)
            {
                connection.Dispose();
                throw new InvalidOperationException(Resources.CannotOpenConnectionWhenGeneratingPrimaryKeys, ex);
            }
            return connection;
        }

        /// <inheritdoc />
        public ISqlExpressionVisitor GetExpressionVisitor() => _sqlExpressionVisitor.Value;

        #endregion

        #region Linq

        /// <summary>
        /// Not implemented.
        /// </summary>
        /// <param name="expression">An expression tree that represents a LINQ query.</param>
        /// <exception cref="NotImplementedException"></exception>
        public IQueryable CreateQuery(Expression expression)
        {
            throw new NotSupportedException(Resources.NonGenericQueryNotSupported);
        }

        /// <summary>
        /// Constructs an <see cref="T:System.Linq.IQueryable`1"></see> object that can evaluate the query represented by a specified expression tree.
        /// </summary>
        /// <typeparam name="TElement">The type of the elements of the <see cref="T:System.Linq.IQueryable`1"></see> that is returned.</typeparam>
        /// <param name="expression">An expression tree that represents a LINQ query.</param>
        /// <returns>
        /// An <see cref="T:System.Linq.IQueryable`1"></see> that can evaluate the query represented by the specified expression tree.
        /// </returns>
        public IQueryable<TElement> CreateQuery<TElement>(Expression expression) => new Query<TElement>(this, expression);

        /// <summary>
        /// Executing non generic result is not actualy supported.
        /// </summary>
        /// <param name="expression">An expression tree that represents a LINQ query.</param>
        /// <exception cref="NotImplementedException"></exception>
        public object Execute(Expression expression)
        {
            throw new NotSupportedException(Resources.NonGenericExecuteNotSupported);
        }

        /// <summary>
        /// Executes the strongly-typed query represented by a specified expression tree.
        /// </summary>
        /// <typeparam name="TResult">The type of the value that results from executing the query.</typeparam>
        /// <param name="expression">An expression tree that represents a LINQ query.</param>
        /// <returns>
        /// The value that results from executing the specified query.
        /// </returns>
        public TResult Execute<TResult>(Expression expression)
            => Execute(new Query<TResult>(this, expression)).FirstOrDefault();

        #endregion

        #region Helpers

        /// <summary>
        /// Gets current the transaction.
        /// </summary>
        /// <returns>Current transaction if is opened; otherwise null;</returns>
        protected DbTransaction GetCurrentTransaction() => _transactionHelper.Value.CurrentTransaction;

        /// <summary>
        /// Connection string na databázu, ktorý bol zadaný pri vytvorení inštancie triedy
        /// (<see cref="QueryProvider(
        /// KormConnectionSettings, ISqlExpressionVisitorFactory, IModelBuilder, ILogger, IDatabaseMapper)"/>).
        /// Ak bola trieda vytvorená konkrétnou inštanciou spojenia, vráti <see langword="null"/>.
        /// </summary>
        protected string ConnectionString { get => _connectionSettings?.ConnectionString; }

        /// <summary>
        /// Vráti, či spojenie na databázu je externé, tzn. či bolo explicitne zadané zvonka v konštruktore.
        /// Ak bolo spojenie zadané explicitne, vráti <see langword="true"/>, ak bol v konštruktore zadaný iba connection
        /// string a spojenie je vytvorené interne, vráti <see langword="false"/>.
        /// </summary>
        protected bool IsExternalConnection { get; }

        /// <summary>
        /// Vráti spojenie na databázu s ktorou trieda pracuje. Ak trieda bola vytvorená iba so zadaným
        /// connection string-om, je vytvorené nové spojenie.
        /// </summary>
        protected DbConnection Connection
        {
            get
            {
                if (_connection == null)
                {
                    _connection = DbProviderFactory.CreateConnection();
                    _connection.ConnectionString = _connectionSettings.ConnectionString;
                }
                return _connection;
            }
        }

        private Data.ConnectionHelper OpenConnection()
        {
            return new Data.ConnectionHelper(Connection);
        }

        private DbCommandInfo CreateCommand<T>(IQuery<T> query)
        {
            DbCommand command = _transactionHelper.Value.CreateCommand();

            SetQueryFilter(query, _sqlExpressionVisitor.Value);

            QueryInfo queryInfo = _sqlExpressionVisitor.Value.GenerateSql(query.Expression);
            command.CommandText = queryInfo.Query;
            ParameterExtractingExpressionVisitor.ExtractParametersToCommand(command, query.Expression);

            return new DbCommandInfo(command, queryInfo.Reader);
        }

        /// <summary>
        /// Sets the default query filter.
        /// </summary>
        /// <typeparam name="T">Entity type.</typeparam>
        /// <param name="query">The query.</param>
        /// <param name="sqlVisitor">The SQL visitor.</param>
        protected internal void SetQueryFilter<T>(IQuery<T> query, ISqlExpressionVisitor sqlVisitor)
            => (query as IQueryBaseInternal).ApplyQueryFilter(_databaseMapper, sqlVisitor);

        private DbCommand CreateCommand(string commandText, CommandParameterCollection parameters)
        {
            DbCommand command = _transactionHelper.Value.CreateCommand();
            command.CommandText = commandText;

            if (parameters?.Count > 0)
            {
                foreach (var parameter in parameters)
                {
                    AddCommandParameter(command, parameter);
                }
            }

            return command;
        }

        private static void AddCommandParameter(DbCommand command, CommandParameter commandParameter)
        {
            DbParameter dbParameter = command.CreateParameter();
            dbParameter.ParameterName = commandParameter.ParameterName;
            dbParameter.Value = commandParameter.Value;
            dbParameter.Direction = commandParameter.Direction;
            if (commandParameter.DataType.HasValue)
            {
                dbParameter.DbType = commandParameter.DataType.Value;
            }
            command.Parameters.Add(dbParameter);
        }

        private DbParameter GetOrAddCommandReturnParameter(DbCommand command)
        {
            DbParameter returnParameter = GetReturnParameter(command);
            if (returnParameter == null)
            {
                returnParameter = command.CreateParameter();
                returnParameter.ParameterName = RETURN_VALUE_PARAM_NAME;
                returnParameter.Value = null;
                returnParameter.Direction = ParameterDirection.ReturnValue;
                command.Parameters.Add(returnParameter);
            }

            return returnParameter;
        }

        private static DbParameter GetReturnParameter(DbCommand command)
        {
            foreach (DbParameter parameter in command.Parameters)
            {
                if (parameter.Direction == ParameterDirection.ReturnValue)
                {
                    return parameter;
                }
            }
            return null;
        }

        private void CheckCommandParameters(CommandParameterCollection parameters)
        {
            if (parameters != null)
            {
                foreach (CommandParameter parameter in parameters)
                {
                    if ((!parameter.DataType.HasValue) && ((parameter.Value == null) || (parameter.Value == DBNull.Value)))
                    {
                        throw new ArgumentException(string.Format(Resources.ParameterDataTypeNotSet, parameter.ParameterName));
                    }
                }
            }
        }

        #endregion

        #region IDisposable

        private bool _disposed = false;

#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }
            if (disposing)
            {
                if ((_connection != null) && (!IsExternalConnection))
                {
                    _connection.Dispose();
                    _connection = null;
                }
            }
            _disposed = true;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member

        #endregion
    }
}
