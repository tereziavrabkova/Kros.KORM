﻿using Kros.KORM.Converter;
using Kros.KORM.Metadata;
using Kros.KORM.Query;
using Kros.KORM.Query.Expressions;
using Kros.KORM.Query.Sql;
using Kros.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text;

namespace Kros.KORM.CommandGenerator
{
    /// <summary>
    /// Generates single-table commands that are used to commit changes made to a DbSet with the associated database.
    /// </summary>
    /// <typeparam name="T">Type class of model.</typeparam>
    internal class CommandGenerator<T> : ICommandGenerator<T>
    {
        #region Constants

        private const string INSERT_QUERY_WITH_OUTPUT =
@"DECLARE @OutputTable TABLE ({0});
INSERT INTO [{1}] ({2}){3} INTO @OutputTable VALUES ({4});
SELECT * FROM @OutputTable;";
        private const string INSERT_QUERY_BASE = "INSERT INTO [{0}] ({1}) VALUES ({2})";
        private const string UPDATE_QUERY_BASE = "UPDATE [{0}] SET {1} WHERE {2}";
        private const string UPSERT_QUERY_BASE = "MERGE INTO [{0}] dst USING(SELECT {1}) src ON {2} {3}{4};";
        private const string UPSERT_QUERY_UPDATE_PART = "WHEN MATCHED THEN UPDATE SET {0} ";
        private const string UPSERT_QUERY_INSERT_PART = "WHEN NOT MATCHED THEN INSERT({0}) VALUES ({1}) ";
        private const string DELETE_QUERY_BASE = "DELETE FROM [{0}] WHERE {1}";
        private const string DELETE_QUERY_BASE_IN = "DELETE FROM [{0}] WHERE [{1}] IN (";
        private const int DEFAULT_MAX_PARAMETERS_FOR_DELETE_COMMANDS_IN_PART = 100;
        private const string OutputStatement = " OUTPUT INSERTED.{0}";

        #endregion

        #region Private Fields

        private readonly TableInfo _tableInfo;
        private readonly Query.IQueryProvider _provider;
        private readonly IQueryBase<T> _query;
        private List<ColumnInfo> _columnsInfo = null;
        private int _maxParametersForDeleteCommandsInPart = DEFAULT_MAX_PARAMETERS_FOR_DELETE_COMMANDS_IN_PART;
        private readonly Lazy<string> _outputStatement;
        private readonly Lazy<string> _outputStatementDefinition;

        #endregion

        #region Public Fields

        /// <summary>
        /// Maximum parameters for delete command in IN part.
        /// </summary>
        public int MaxParametersForDeleteCommandsInPart
        {
            get => _maxParametersForDeleteCommandsInPart;
            set => _maxParametersForDeleteCommandsInPart = value < 1 ? 1 : value;
        }

        #endregion

        #region Ctor

        /// <summary>
        /// Initializes a new instance of the <see cref="CommandGenerator{T}" /> class.
        /// </summary>
        /// <param name="tableInfo">Information about table from database.</param>
        /// <param name="provider">Provider, that can execute queries.</param>
        /// <param name="query">Executing query.</param>
        public CommandGenerator(TableInfo tableInfo, KORM.Query.IQueryProvider provider, IQueryBase<T> query)
        {
            Check.NotNull(tableInfo, nameof(tableInfo));
            Check.NotNull(provider, nameof(provider));
            Check.NotNull(query, nameof(query));

            _tableInfo = tableInfo;
            _provider = provider;
            _query = query;
            _outputStatement = new Lazy<string>(() => GetOutputStatement());
            _outputStatementDefinition = new Lazy<string>(() => GetOutputStatementDefinition());
        }

        #endregion

        #region ICommandGenerator

        /// <summary>
        /// Gets the automatically generated DbCommand object required to perform insertions on the database.
        /// </summary>
        /// <returns>Insert command.</returns>
        public DbCommand GetInsertCommand()
        {
            IEnumerable<ColumnInfo> columns = GetQueryColumnsForInsert();
            DbCommand cmd = _provider.GetCommandForCurrentTransaction();
            AddParametersToCommand(cmd, columns);
            cmd.CommandText = GetInsertCommandText(columns);

            return cmd;
        }

        private IEnumerable<ColumnInfo> GetQueryColumnsForInsert()
            => _tableInfo.HasIdentityPrimaryKey
            ? GetQueryColumns(ValueGenerated.OnInsert).Where(p => !p.IsPrimaryKey)
            : GetQueryColumns(ValueGenerated.OnInsert);

        /// <summary>
        /// Gets the automatically generated DbCommand object required to perform updates on the database
        /// </summary>
        /// <exception cref="Exceptions.MissingPrimaryKeyException">Table does not have primary key.</exception>
        /// <returns>Update command.</returns>
        public DbCommand GetUpdateCommand()
        {
            ThrowHelper.CheckAndThrowMethodNotSupportedWhenNoPrimaryKey(_tableInfo);

            IEnumerable<ColumnInfo> columns = GetQueryColumns(ValueGenerated.OnUpdate);
            DbCommand cmd = _provider.GetCommandForCurrentTransaction();
            AddParametersToCommand(cmd, columns.Where(x => !x.IsPrimaryKey));
            AddParametersToCommand(cmd, columns.Where(x => x.IsPrimaryKey));
            cmd.CommandText = GetUpdateCommandText(columns);
            return cmd;
        }

        /// <summary>
        /// Gets the upsert command.
        /// </summary>
        /// <returns>
        /// Upsert command.
        /// </returns>
        public DbCommand GetUpsertCommand(IEnumerable<string> conditionColumnNames = null)
        {
            IEnumerable<ColumnInfo> conditionColumns;
            if (conditionColumnNames?.Any() == true)
            {
                ThrowHelper.CheckAndThrowColumnDoesNotExists(_tableInfo, conditionColumnNames);
                var columnNamesSet = new HashSet<string>(conditionColumnNames, StringComparer.OrdinalIgnoreCase);
                conditionColumns = GetQueryColumns().Where(c => columnNamesSet.Contains(c.Name));
            }
            else
            {
                ThrowHelper.CheckAndThrowMethodNotSupportedWhenNoPrimaryKey(_tableInfo);
                conditionColumns = GetQueryColumns().Where(c => c.IsPrimaryKey);
            }

            return GetUpsertCommandInternal(conditionColumns);
        }

        /// <summary>
        /// Gets the automatically generated DbCommand object required to perform deletions on the database.
        /// </summary>
        /// <exception cref="Exceptions.MissingPrimaryKeyException">Table does not have primary key.</exception>
        /// <returns>Delete command.</returns>
        public DbCommand GetDeleteCommand()
        {
            ThrowHelper.CheckAndThrowMethodNotSupportedWhenNoPrimaryKey(_tableInfo);

            IEnumerable<ColumnInfo> columns = _tableInfo.PrimaryKey;
            DbCommand cmd = _provider.GetCommandForCurrentTransaction();
            AddParametersToCommand(cmd, columns);
            cmd.CommandText = GetDeleteCommandText(columns);
            return cmd;
        }

        /// <inheritdoc />
        public DbCommand GetDeleteCommand(WhereExpression whereExpression)
        {
            DbCommand cmd = _provider.GetCommandForCurrentTransaction();

            cmd.CommandText = string.Format(DELETE_QUERY_BASE, _tableInfo.Name, whereExpression.Sql);
            ParameterExtractingExpressionVisitor.ExtractParametersToCommand(cmd, whereExpression);

            return cmd;
        }

        /// <inheritdoc />
        public IEnumerable<DbCommand> GetDeleteCommands(IEnumerable ids)
        {
            ThrowHelper.CheckAndThrowMethodNotSupportedWhenNoPrimaryKey(_tableInfo);
            ThrowHelper.CheckAndThrowMethodNotSupportedForCompositePrimaryKey(_tableInfo);

            var retVal = new List<DbCommand>();
            ColumnInfo colInfo = _tableInfo.PrimaryKey.First();
            DbCommand cmd = null;
            var deleteQueryText = new StringBuilder();
            int iterationCount = 0;

            foreach (object id in ids)
            {
                if (iterationCount == 0)
                {
                    cmd = _provider.GetCommandForCurrentTransaction();
                    deleteQueryText.Clear();
                    deleteQueryText.AppendFormat(DELETE_QUERY_BASE_IN, _tableInfo.Name, colInfo.Name);
                }

                iterationCount++;
                string paramterName = $"@P{iterationCount}";
                AddDeleteCommandParameter(cmd, paramterName, id);
                if (iterationCount > 1)
                {
                    deleteQueryText.Append(",");
                }
                deleteQueryText.Append(paramterName);

                if (iterationCount == MaxParametersForDeleteCommandsInPart)
                {
                    retVal.Add(FinishDeleteCommand(cmd, deleteQueryText));
                    iterationCount = 0;
                }
            }

            if (iterationCount > 0)
            {
                retVal.Add(FinishDeleteCommand(cmd, deleteQueryText));
            }

            return retVal;
        }

        /// <summary>
        /// Fills command's parameters with values from <paramref name="item" />.
        /// </summary>
        /// <param name="command">Command which parameters are filled.</param>
        /// <param name="item">Item, from which command is filled.</param>
        /// <param name="valueGenerated">Indicates when a value for a property will be generated by the database.</param>
        /// <param name="ignoreValueGenerators">Indicates whether to ignore value generators.</param>
        /// <exception cref="System.ArgumentNullException">Either <paramref name="command" /> or <paramref name="item" />
        /// is <see langword="null"/>.</exception>
        public void FillCommand(DbCommand command, T item, ValueGenerated valueGenerated, bool ignoreValueGenerators = false)
        {
            Check.NotNull(command, nameof(command));
            Check.NotNull(item, nameof(item));

            foreach (ColumnInfo colInfo in GetQueryColumns(valueGenerated))
            {
                string paramName = $"@{colInfo.Name}";
                if (command.Parameters.Contains(paramName))
                {
                    DbParameter parameter = command.Parameters[paramName];
                    if (!ignoreValueGenerators)
                    {
                        SetColumnValueFromValueGenerator(colInfo, item, valueGenerated);
                    }
                    object val = GetColumnValue(colInfo, item, valueGenerated);
                    parameter.Value = val ?? System.DBNull.Value;
                }
            }
        }

        private void AddDeleteCommandParameter(DbCommand cmd, string parameterName, object value)
        {
            DbParameter newParameter = cmd.CreateParameter();
            newParameter.ParameterName = parameterName;
            newParameter.Value = value;
            cmd.Parameters.Add(newParameter);
        }

        private DbCommand FinishDeleteCommand(DbCommand cmd, StringBuilder deleteQueryText)
        {
            deleteQueryText.Append(")");
            cmd.CommandText = deleteQueryText.ToString();
            return cmd;
        }

        #endregion

        #region Private Helpers

        /// <inheritdoc/>
        public IEnumerable<ColumnInfo> GetQueryColumns(ValueGenerated valueGenerated)
            => GetQueryColumns().Where(c => c.ValueGenerator == null || c.ValueGenerated.HasFlag(valueGenerated));

        private IEnumerable<ColumnInfo> GetQueryColumns()
        {
            if (_columnsInfo == null)
            {
                _columnsInfo = new List<ColumnInfo>();
                var expression = (_query.Expression as SelectExpression);
                var columns = expression.ColumnsExpression.ColumnsPart.Split(',');

                foreach (string column in columns)
                {
                    ColumnInfo columnInfo = _tableInfo.GetColumnInfo(column.Trim());

                    if (columnInfo != null)
                    {
                        _columnsInfo.Add(columnInfo);
                    }
                }
            }

            return _columnsInfo;
        }

        private void AddParametersToCommand(DbCommand cmd, IEnumerable<ColumnInfo> columns)
        {
            foreach (ColumnInfo colInfo in columns)
            {
                DbParameter par = cmd.CreateParameter();
                par.ParameterName = $"@{colInfo.Name}";
                _provider.SetParameterDbType(par, _tableInfo.Name, colInfo.Name);
                cmd.Parameters.Add(par);
            }
        }

        /// <inheritdoc/>
        public object GetColumnValue(ColumnInfo columnInfo, T item, ValueGenerated valueGenerated)
        {
            object value = columnInfo.PropertyInfo.GetValue(item, null);
            IConverter converter = ConverterHelper.GetConverter(columnInfo, value?.GetType());
            if (converter != null)
            {
                value = converter.ConvertBack(value);
            }
            return value;
        }

        internal void SetColumnValueFromValueGenerator(ColumnInfo columnInfo, T item, ValueGenerated valueGenerated)
        {
            if ((!HasValueGeneratorOnInsert(columnInfo) || valueGenerated != ValueGenerated.OnUpdate) &&
                TryGetValueFromValueGenerators(columnInfo, valueGenerated, out object generatorValue))
            {
                columnInfo.PropertyInfo.SetValue(item, generatorValue);
            }
        }

        private bool HasValueGeneratorOnInsert(ColumnInfo columnInfo)
            => (columnInfo.ValueGenerator != null && columnInfo.ValueGenerated == ValueGenerated.OnInsert);

        private bool TryGetValueFromValueGenerators(ColumnInfo columnInfo, ValueGenerated valueGenerated, out object value)
        {
            value = null;

            if (columnInfo.ValueGenerated.HasFlag(valueGenerated))
            {
                IValueGenerator valueGenerator = columnInfo.ValueGenerator;
                if (valueGenerator != null)
                {
                    value = valueGenerator.GetValue();
                    return true;
                }
            }

            return false;
        }

        private string GetInsertCommandText(IEnumerable<ColumnInfo> columns)
        {
            var paramNames = new StringBuilder();
            var paramValues = new StringBuilder();

            foreach (ColumnInfo column in columns)
            {
                if (paramNames.Length > 0)
                {
                    paramNames.Append(", ");
                }
                paramNames.AppendFormat("[{0}]", column.Name);

                if (paramValues.Length > 0)
                {
                    paramValues.Append(", ");
                }
                paramValues.AppendFormat("@{0}", column.Name);
            }

            return _tableInfo.HasIdentityPrimaryKey
                ? GetInsertCommandTextWithOutput(paramNames.ToString(), paramValues.ToString())
                : GetInsertCommandText(paramNames.ToString(), paramValues.ToString());
        }

        private string GetInsertCommandTextWithOutput(string paramNames, string paramValues)
            => string.Format(INSERT_QUERY_WITH_OUTPUT,
                _outputStatementDefinition.Value,
               _tableInfo.Name,
               paramNames,
               _outputStatement.Value,
               paramValues);

        private string GetInsertCommandText(string paramNames, string paramValues)
            => string.Format(INSERT_QUERY_BASE,
               _tableInfo.Name,
               paramNames,
               paramValues);

        private string GetOutputStatement()
            => _tableInfo.HasIdentityPrimaryKey
            ? string.Format(OutputStatement, _tableInfo.IdentityPrimaryKey.Name)
            : string.Empty;

        private string GetOutputStatementDefinition()
            => _tableInfo.HasIdentityPrimaryKey
            ? _tableInfo.IdentityPrimaryKey.Name + " " + _tableInfo.IdentityPrimaryKeySqlType
            : string.Empty;

        private string GetUpdateCommandText(IEnumerable<ColumnInfo> columns)
        {
            var paramSetPart = new StringBuilder();

            foreach (ColumnInfo column in columns.Where(p => !p.IsPrimaryKey))
            {
                if (paramSetPart.Length > 0)
                {
                    paramSetPart.Append(", ");
                }
                paramSetPart.AppendFormat("[{0}] = @{0}", column.Name);
            }

            var paramWherePart = new StringBuilder();

            foreach (ColumnInfo col in _tableInfo.PrimaryKey)
            {
                if (paramWherePart.Length > 0)
                {
                    paramWherePart.Append(" AND ");
                }
                paramWherePart.AppendFormat("([{0}] = @{0})", col.Name);
            }

            return string.Format(UPDATE_QUERY_BASE, _tableInfo.Name, paramSetPart.ToString(), paramWherePart.ToString());
        }

        private DbCommand GetUpsertCommandInternal(IEnumerable<ColumnInfo> conditionColumns)
        {
            IEnumerable<ColumnInfo> columns = GetQueryColumns(ValueGenerated.OnUpdate);
            DbCommand cmd = _provider.GetCommandForCurrentTransaction();
            AddParametersToCommand(cmd, columns.Where(x => !x.IsPrimaryKey));
            AddParametersToCommand(cmd, columns.Where(x => x.IsPrimaryKey));

            cmd.CommandText = GetUpsertCommandText(conditionColumns);
            return cmd;
        }

        private string GetUpsertCommandText(IEnumerable<ColumnInfo> conditionColumns)
        {
            IEnumerable<ColumnInfo> updateColumns = GetQueryColumns(ValueGenerated.OnUpdate);
            IEnumerable<ColumnInfo> insertColumns = GetQueryColumns(ValueGenerated.OnInsert);

            IEnumerable<string> sourceSelectPart = conditionColumns.Select(c => $"@{c.Name} {c.Name}");
            IEnumerable<string> sourceConditionPart = conditionColumns.Select(c => $"src.[{c.Name}] = dst.[{c.Name}]");
            IEnumerable<string> insertColumnsPart = insertColumns.Select(c => $"[{c.Name}]");
            IEnumerable<string> insertValuesPart = insertColumns.Select(c => $"@{c.Name}");
            IEnumerable<string> updateSetPart = updateColumns.Where(c =>
                !c.IsPrimaryKey && !conditionColumns.Contains(c)).Select(c => $"[{c.Name}] = @{c.Name}");

            string updatePart = string.Join(", ", updateSetPart);
            if (!string.IsNullOrEmpty(updatePart))
            {
                updatePart = string.Format(UPSERT_QUERY_UPDATE_PART, updatePart);
            }
            string insertPart = string.Format(
                UPSERT_QUERY_INSERT_PART,
                string.Join(", ", insertColumnsPart),
                string.Join(", ", insertValuesPart));

            return string.Format(
                UPSERT_QUERY_BASE,
                _tableInfo.Name,
                string.Join(", ", sourceSelectPart),
                string.Join(" AND ", sourceConditionPart),
                updatePart ?? string.Empty,
                insertPart);
        }

        private string GetDeleteCommandText(IEnumerable<ColumnInfo> columns)
        {
            var paramWherePart = new StringBuilder();

            foreach (ColumnInfo column in columns)
            {
                if (paramWherePart.Length > 0)
                {
                    paramWherePart.Append(" AND ");
                }
                paramWherePart.AppendFormat("([{0}] = @{0})", column.Name);
            }

            return string.Format(DELETE_QUERY_BASE, _tableInfo.Name, paramWherePart.ToString());
        }

        #endregion
    }
}
