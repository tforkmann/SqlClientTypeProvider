namespace SqlClientTypeProvider.Common

open System
open System.Collections.Generic
open System.ComponentModel
open System.Data
open System.Data.Common
open System.IO
open System.Linq.Expressions
open System.Reflection
open System.Text
open SqlClientTypeProvider.Operators 
open SqlClientTypeProvider.Transactions
open SqlClientTypeProvider.Schema
open Microsoft.FSharp.Reflection
open System.Collections.Concurrent

type DatabaseProviderTypes =
    | MSSQLSERVER = 0
    // | SQLITE = 1
    // | POSTGRESQL = 2
    // | MYSQL = 3
    // | ORACLE = 4
    // | MSACCESS = 5
    // | ODBC = 6
    // | FIREBIRD = 7
type RelationshipDirection = Children = 0 | Parents = 1

type CaseSensitivityChange =
    | ORIGINAL = 0
    | TOUPPER = 1
    | TOLOWER = 2

type OdbcQuoteCharacter =
    | DEFAULT_QUOTE = 0
    /// MySQL/Postgre style: `alias` 
    | GRAVE_ACCENT = 1
    /// Microsoft SQL style: [alias]
    | SQUARE_BRACKETS = 2
    /// Plain, no special names: alias
    | NO_QUOTES = 3 // alias
    /// Amazon Redshift style: "alias" & Firebird style too
    | DOUBLE_QUOTES = 4
    /// Single quote: 'alias'
    | APHOSTROPHE = 5 

type SQLiteLibrary =
    // .NET Framework default
    | SystemDataSQLite = 0
    // Mono version
    | MonoDataSQLite = 1
    // Auto-select by environment
    | AutoSelect = 2
    // Microsoft.Data.Sqlite. May support .NET Standard 2.0 contract in the future.
    | MicrosoftDataSqlite = 3

module public QueryEvents =
      
   type SqlEventData = {
       /// The text of the SQL command being executed.
       Command: string

       /// The parameters (if any) passed to the SQL command being executed.
       Parameters: (string*obj) seq

       /// The SHA256 hash of the UTF8-encoded connection string used to perform this command.
       /// Use this to determine on which database connection the command is going to be executed.
       ConnectionStringHash: byte[]      
   }
   with 
      override x.ToString() =
        let paramsString = x.Parameters |> Seq.fold (fun acc (pName, pValue) -> acc + (sprintf "%s - %A; " pName pValue)) ""
        sprintf "%s -- params %s" x.Command paramsString
      
      /// Use this to execute similar queries to test the result of the executed query.
      member x.ToRawSql() =
        x.Parameters |> Seq.fold (fun (acc:string) (pName, pValue) -> 
            match pValue with
            | :? String as pv -> acc.Replace(pName, (sprintf "'%s'" (pv.Replace("'", "''"))))
            | :? DateTime as pv -> acc.Replace(pName, (sprintf "'%s'" (pv.ToString("yyyy-MM-dd hh:mm:ss"))))
            | _ -> acc.Replace(pName, (sprintf "%O" pValue))) x.Command

   let private sqlEvent = new Event<SqlEventData>()
   
   [<CLIEvent>]
   /// This event fires immediately before the execution of every generated query. 
   /// Listen to this event to display or debug the content of your queries.
   let SqlQueryEvent = sqlEvent.Publish

   let private publishSqlQuery = 
      
      let connStrHashCache = ConcurrentDictionary<string, byte[]>()

      fun connStr qry parameters ->
        
        let hashValue = connStrHashCache.GetOrAdd(connStr, fun str -> Text.Encoding.UTF8.GetBytes(str : string) |> Bytes.sha256)

        sqlEvent.Trigger { Command = qry
                           ConnectionStringHash = hashValue
                           Parameters = parameters
                         }

   let internal PublishSqlQuery connStr qry (spc:IDbDataParameter seq) = 
      publishSqlQuery connStr qry (spc |> Seq.map(fun p -> p.ParameterName, p.Value))

   let internal PublishSqlQueryCol connStr qry (spc:DbParameterCollection) = 
      publishSqlQuery connStr qry [ for p in spc -> (p.ParameterName, p.Value) ]

   let internal PublishSqlQueryICol connStr qry (spc:IDataParameterCollection) = 
      publishSqlQuery connStr qry [ for op in spc do
                                      let p = op :?> IDataParameter
                                      yield (p.ParameterName, p.Value)]


   let private expressionEvent = new Event<System.Linq.Expressions.Expression>()
   
   [<CLIEvent>]
   let LinqExpressionEvent = expressionEvent.Publish

   let internal publishExpression(e) = expressionEvent.Trigger(e)

   
type EntityState =
    | Unchanged
    | Created
    | Modified of string list
    | Delete
    | Deleted

type OnConflict = 
    /// Throws an exception if the primary key already exists (default behaviour).
    | Throw
    /// If the primary key already exists, updates the existing row's columns to match the new entity.
    /// Currently supported only on PostgreSQL 9.5+
    | Update
    /// If the primary key already exists, leaves the existing row unchanged.
    /// Currently supported only on PostgreSQL 9.5+
    | DoNothing

type MappedColumnAttribute(name: string) = 
    inherit Attribute()
    member x.Name with get() = name

[<System.Runtime.Serialization.DataContract(Name = "SqlEntity", Namespace = "http://schemas.microsoft.com/sql/2011/Contracts"); DefaultMember("Item")>]
type SqlEntity(dc: ISqlDataContext, tableName, columns: ColumnLookup) =
    let table = Table.FromFullName tableName
    let propertyChanged = Event<_,_>()

    let data = Dictionary<string,obj>()
    let aliasCache = new ConcurrentDictionary<string,SqlEntity>(HashIdentity.Structural)

    let replaceFirst (text:string) (oldValue:string) (newValue) =
        let position = text.IndexOf oldValue
        if position < 0 then
            text
        else
            text.Substring(0, position) + newValue + text.Substring(position + oldValue.Length)

    member val _State = Unchanged with get, set

    member e.Delete() =
        e._State <- Delete
        dc.SubmitChangedEntity e

    member internal e.TriggerPropertyChange(name) = propertyChanged.Trigger(e, PropertyChangedEventArgs(name))
    member internal __.ColumnValuesWithDefinition = seq { for kvp in data -> kvp.Key, kvp.Value, columns.TryFind(kvp.Key) }

    member __.ColumnValues = seq { for kvp in data -> kvp.Key, kvp.Value }
    member __.HasColumn(key, ?comparison)= 
        let comparisonOption = defaultArg comparison StringComparison.InvariantCulture
        columns |> Seq.exists(fun kp -> (kp.Key |> SchemaProjections.buildFieldName).Equals(key, comparisonOption))
    member __.Table= table
    member __.DataContext with get() = dc

    member __.GetColumn<'T>(key) : 'T =
        let defaultValue() =
            if typeof<'T> = typeof<string> then (box String.Empty) :?> 'T
            else Unchecked.defaultof<'T>
        if data.ContainsKey key then
           match data.[key] with
           | null -> defaultValue()
           | :? System.DBNull -> defaultValue()
           // Postgres array types
           | :? Array as arr -> 
                unbox arr
           // This deals with an oracle specific case where the type mappings says it returns a System.Decimal but actually returns a float!?!?!  WTF...           
           | data when typeof<'T> <> data.GetType() && 
                       typeof<'T> <> typeof<obj>
                -> unbox <| Convert.ChangeType(data, typeof<'T>)
           | data -> unbox data
        else defaultValue()

    member __.GetColumnOption<'T>(key) : Option<'T> =
       if data.ContainsKey key then
           match data.[key] with
           | null -> None
           | :? System.DBNull -> None
           | data when data.GetType() <> typeof<'T> && typeof<'T> <> typeof<obj> -> Some(unbox<'T> <| Convert.ChangeType(data, typeof<'T>))
           | data -> Some(unbox data)
       else None

    member __.GetPkColumnOption<'T>(keys: string list) : 'T list =
        keys |> List.choose(fun key -> 
            __.GetColumnOption<'T>(key)) 

    member internal this.GetColumnOptionWithDefinition(key) =
        this.GetColumnOption(key) |> Option.bind (fun v -> Some(box v, columns.TryFind(key)))

    member private e.UpdateField key =
        match e._State with
        | Modified fields ->
            e._State <- Modified (key::fields)
            e.DataContext.SubmitChangedEntity e
        | Unchanged ->
            e._State <- Modified [key]
            e.DataContext.SubmitChangedEntity e
        | Deleted | Delete -> failwith ("You cannot modify an entity that is pending deletion: " + key)
        | Created -> ()

    member __.SetColumnSilent(key,value) =
        data.[key] <- value

    member __.SetPkColumnSilent(keys,value) =
        keys |> List.iter(fun x -> data.[x] <- value)

    member e.SetColumn<'t>(key,value : 't) =
        data.[key] <- value
        e.UpdateField key
        e.TriggerPropertyChange key

    member e.SetData(data) = data |> Seq.iter e.SetColumnSilent

    member __.SetColumnOptionSilent(key,value) =
      match value with
      | Some value ->
          if not (data.ContainsKey key) then data.Add(key,value)
          else data.[key] <- value
      | None -> data.Remove key |> ignore

    member __.SetPkColumnOptionSilent(keys,value) =
        keys |> List.iter(fun x -> 
            match value with
            | Some value ->
                if not (data.ContainsKey x) then data.Add(x,value)
                else data.[x] <- value
            | None -> data.Remove x |> ignore)

    member e.SetColumnOption(key,value) =
      match value with
      | Some value ->
          if not (data.ContainsKey key) then data.Add(key,value)
          else data.[key] <- value
          e.TriggerPropertyChange key
      | None -> if data.Remove key then e.TriggerPropertyChange key
      e.UpdateField key

    member __.HasValue(key) = data.ContainsKey key

    /// creates a new SQL entity from alias data in this entity
    member internal e.GetSubTable(alias:string,tableName) =
        aliasCache.GetOrAdd(alias, fun alias ->
            let tableName = if tableName <> "" then tableName else e.Table.FullName
            let newEntity = SqlEntity(dc, tableName, columns)
            // attributes names cannot have a period in them unless they are an alias
            let pred =
                let prefix = "[" + alias + "]."
                let prefix2 = alias + "."
                let prefix3 = "`" + alias + "`."
                let prefix4 = alias + "_"
                let prefix5 = alias.ToUpper() + "_"
                (fun (k:string,v) ->
                    if k.StartsWith prefix then
                        let temp = replaceFirst k prefix ""
                        let temp = temp.Substring(1,temp.Length-2)
                        Some(temp,v)
                    // this case is for PostgreSQL and other vendors that use " as whitespace qualifiers
                    elif  k.StartsWith prefix2 then
                        let temp = replaceFirst k prefix2 ""
                        Some(temp,v)
                    // this case is for MySQL and other vendors that use ` as whitespace qualifiers
                    elif  k.StartsWith prefix3 then
                        let temp = replaceFirst k prefix3 ""
                        let temp = temp.Substring(1,temp.Length-2)
                        Some(temp,v)
                    //this case for MSAccess, uses _ as whitespace qualifier
                    elif  k.StartsWith prefix4 then
                        let temp = replaceFirst k prefix4 ""
                        Some(temp,v)
                    //this case for Firebird version<=2.1, all uppercase
                    elif  k.StartsWith prefix5 then 
                        let temp = replaceFirst k prefix5 ""
                        Some(temp,v)
                    elif not(String.IsNullOrEmpty(k)) then // this is for dynamic alias columns: [a].[City] as City
                        Some(k,v)
                    else None)

            e.ColumnValues
            |> Seq.choose pred
            |> Seq.iter( fun (k,v) -> newEntity.SetColumnSilent(k,v))

            newEntity)

    member x.MapTo<'a>(?propertyTypeMapping : (string * obj) -> obj) =
        let typ = typeof<'a>
        let propertyTypeMapping = defaultArg propertyTypeMapping snd
        let cleanName (n:string) = n.Replace("_","").Replace(" ","").ToLower()
        let clean (pi: PropertyInfo) = 
            match pi.GetCustomAttribute(typeof<MappedColumnAttribute>) with
            | :? MappedColumnAttribute as attr -> attr.Name
            | _ -> pi.Name
            |> cleanName
        let dataMap = x.ColumnValues |> Seq.map (fun (n,v) -> cleanName n, v) |> dict
        if FSharpType.IsRecord typ
        then
            let ctor = FSharpValue.PreComputeRecordConstructor(typ)
            let fields = FSharpType.GetRecordFields(typ)
            let values =
                [|
                    for prop in fields do
                        match dataMap.TryGetValue(clean prop) with
                        | true, data -> yield propertyTypeMapping (prop.Name,data)
                        | false, _ -> ()
                |]
            unbox<'a> (ctor(values))
        else
            let instance = Activator.CreateInstance<'a>()
            for prop in typ.GetProperties() do
                match dataMap.TryGetValue(clean prop) with
                | true, data -> prop.GetSetMethod().Invoke(instance, [|propertyTypeMapping (prop.Name,data)|]) |> ignore
                | false, _ -> ()
            instance
    
    /// Attach/copy entity to a different data-context.
    /// Second parameter: SQL UPDATE or INSERT clause?  
    /// UPDATE: Updates the exising database entity with the values that this entity contains.
    /// INSERT: Makes a copy of entity (database row), which is a new row with the same columns and values (except Id)
    member __.CloneTo(secondContext, itemExistsAlready:bool) = 
        let newItem = SqlEntity(secondContext, tableName, columns)
        if itemExistsAlready then 
            newItem.SetData(data |> Seq.map(fun kvp -> kvp.Key, kvp.Value))
            newItem._State <- Modified (data |> Seq.toList 
                                             |> List.map(fun kvp -> kvp.Key)
                                             |> List.filter(fun k -> k <> "Id"))
        else 
            newItem.SetData(data 
                      |> Seq.filter(fun kvp -> kvp.Key <> "Id" && not (isNull kvp.Value)) 
                      |> Seq.map(fun kvp -> kvp.Key, kvp.Value))
            newItem._State <- Created
        newItem

    /// Makes a copy of entity (database row), which is a new row with the same columns and values (except Id)
    /// If column primaty key is something else and not-auto-generated, then, too bad...
    member __.Clone() = 
        __.CloneTo(dc, false)

    /// Determines what should happen when saving this entity if it is newly-created but another entity with the same primary key already exists
    member val OnConflict = Throw with get, set

    interface System.ComponentModel.INotifyPropertyChanged with
        [<CLIEvent>] member __.PropertyChanged = propertyChanged.Publish

    interface System.ComponentModel.ICustomTypeDescriptor with
        member e.GetComponentName() = TypeDescriptor.GetComponentName(e,true)
        member e.GetDefaultEvent() = TypeDescriptor.GetDefaultEvent(e,true)
        member e.GetClassName() = e.Table.FullName
        member e.GetEvents(_) = TypeDescriptor.GetEvents(e,true)
        member e.GetEvents() = TypeDescriptor.GetEvents(e,null,true)
        member e.GetConverter() = TypeDescriptor.GetConverter(e,true)
        member __.GetPropertyOwner(_) = upcast data
        member e.GetAttributes() = TypeDescriptor.GetAttributes(e,true)
        member e.GetEditor(typeBase) = TypeDescriptor.GetEditor(e,typeBase,true)
        member __.GetDefaultProperty() = null
        member e.GetProperties()  = (e :> ICustomTypeDescriptor).GetProperties(null)
        member __.GetProperties(_) =
            PropertyDescriptorCollection(
               data
               |> Seq.map(
                  function KeyValue(k,v) ->
                              { new PropertyDescriptor(k,[||])  with
                                 override __.PropertyType with get() = v.GetType()
                                 override __.SetValue(e,value) = (e :?> SqlEntity).SetColumn(k,value)
                                 override __.GetValue(e) = (e:?>SqlEntity).GetColumn k
                                 override __.IsReadOnly with get() = false
                                 override __.ComponentType with get () = null
                                 override __.CanResetValue(_) = false
                                 override __.ResetValue(_) = ()
                                 override __.ShouldSerializeValue(_) = false })
               |> Seq.cast<PropertyDescriptor> |> Seq.toArray )

and ResultSet = seq<(string * obj)[]>
and ReturnSetType =
    | ScalarResultSet of string * obj
    | ResultSet of string * ResultSet
and ReturnValueType =
    | Unit
    | Scalar of string * obj
    | SingleResultSet of string * ResultSet
    | Set of seq<ReturnSetType>

and ISqlDataContext =
    abstract ConnectionString           : string
    abstract CommandTimeout             : Option<int>
    abstract CreateRelated              : SqlEntity * string * string * string * string * string * RelationshipDirection -> System.Linq.IQueryable<SqlEntity>
    abstract CreateEntities             : string -> System.Linq.IQueryable<SqlEntity>
    abstract CallSproc                  : RunTimeSprocDefinition * QueryParameter[] * obj[] -> obj
    abstract CallSprocAsync             : RunTimeSprocDefinition * QueryParameter[] * obj[] -> Async<SqlEntity>
    abstract GetIndividual              : string * obj -> SqlEntity
    abstract SubmitChangedEntity        : SqlEntity -> unit
    abstract SubmitPendingChanges       : unit -> unit
    abstract SubmitPendingChangesAsync  : unit -> Async<unit>
    abstract ClearPendingChanges        : unit -> unit
    abstract GetPendingEntities         : unit -> SqlEntity list
    abstract GetPrimaryKeyDefinition    : string -> string
    abstract CreateConnection           : unit -> IDbConnection
    abstract CreateEntity               : string -> SqlEntity
    abstract ReadEntities               : string * ColumnLookup * IDataReader -> SqlEntity[]
    abstract ReadEntitiesAsync          : string * ColumnLookup * DbDataReader -> Async<SqlEntity[]>
    abstract SqlOperationsInSelect      : SelectOperations
    abstract SaveContextSchema          : string -> unit

// LinkData is for joins with SelectMany
and LinkData =
    { PrimaryTable       : Table
      PrimaryKey         : SqlColumnType list
      ForeignTable       : Table
      ForeignKey         : SqlColumnType list
      OuterJoin          : bool
      RelDirection       : RelationshipDirection      }
    with
        member x.Rev() =
            { x with PrimaryTable = x.ForeignTable; PrimaryKey = x.ForeignKey; ForeignTable = x.PrimaryTable; ForeignKey = x.PrimaryKey }

// GroupData is for group-by projections
and GroupData =
    { PrimaryTable       : Table
      KeyColumns         : (alias * SqlColumnType) list
      AggregateColumns   : (alias * SqlColumnType) list
      Projection         : Expression option }

and table = string

and SelectData = LinkQuery of LinkData | GroupQuery of GroupData | CrossJoin of alias * Table
and UnionType = NormalUnion | UnionAll | Intersect | Except
and internal SqlExp =
    | BaseTable    of alias * Table                         // name of the initiating IQueryable table - this isn't always the ultimate table that is selected
    | SelectMany   of alias * alias * SelectData * SqlExp   // from alias, to alias and join data including to and from table names. Note both the select many and join syntax end up here
    | FilterClause of Condition * SqlExp                    // filters from the where clause(es)
    | HavingClause of Condition * SqlExp                    // filters from the where clause(es)
    | Projection   of Expression * SqlExp                   // entire LINQ projection expression tree
    | Distinct     of SqlExp                                // distinct indicator
    | OrderBy      of alias * SqlColumnType * bool * SqlExp // alias and column name, bool indicates ascending sort
    | Union        of UnionType * string * seq<IDbDataParameter> * SqlExp  // union type and subquery
    | Skip         of int * SqlExp
    | Take         of int * SqlExp
    | Count        of SqlExp
    | AggregateOp  of alias * SqlColumnType * SqlExp
    with 
        member this.HasAutoTupled() =
            let rec aux = function
                | BaseTable(_) -> false
                | SelectMany(_) -> true
                | FilterClause(_,rest)
                | HavingClause(_,rest)
                | Projection(_,rest)
                | Distinct rest
                | OrderBy(_,_,_,rest)
                | Skip(_,rest)
                | Take(_,rest)
                | Union(_,_,_,rest)
                | Count(rest) 
                | AggregateOp(_,_,rest) -> aux rest
            aux this
        member this.HasGroupBy() =
            let rec isGroupBy = function
                | SelectMany(_, _,GroupQuery(gdata),_) -> Some (gdata.PrimaryTable, gdata.KeyColumns)
                | BaseTable(_) -> None
                | SelectMany(_) -> None
                | FilterClause(_,rest)
                | HavingClause(_,rest)
                | Projection(_,rest)
                | Distinct rest
                | OrderBy(_,_,_,rest)
                | Skip(_,rest)
                | Take(_,rest)
                | Union(_,_,_,rest)
                | Count(rest) 
                | AggregateOp(_,_,rest) -> isGroupBy rest
            isGroupBy this

and internal SqlQuery =
    { Filters       : Condition list
      HavingFilters : Condition list
      Links         : (alias * LinkData * alias) list
      CrossJoins    : (alias * Table) list
      Aliases       : Map<string, Table>
      Ordering      : (alias * SqlColumnType * bool) list
      Projection    : Expression list
      Grouping      : (list<alias * SqlColumnType> * list<alias * SqlColumnType>) list //key columns, aggregate columns
      Distinct      : bool
      UltimateChild : (string * Table) option
      Skip          : int option
      Take          : int option
      Union         : (UnionType*string*seq<IDbDataParameter>) option
      Count         : bool 
      AggregateOp   : (alias * SqlColumnType) list }
    with
        static member Empty = { Filters = []; Links = []; Grouping = []; Aliases = Map.empty; Ordering = []; Count = false; AggregateOp = []; CrossJoins = []
                                Projection = []; Distinct = false; UltimateChild = None; Skip = None; Take = None; Union = None; HavingFilters = [] }

        static member OfSqlExp(exp,entityIndex: string ResizeArray) =
            let legaliseName (alias:alias) =
                if alias.StartsWith("_") then alias.TrimStart([|'_'|]) else alias

            let rec convert (q:SqlQuery) = function
                | BaseTable(a,e) -> match q.UltimateChild with
                                        | Some(_) when q.CrossJoins.IsEmpty -> q
                                        | None when q.Links.Length > 0 && not(q.Links |> List.exists(fun (a',_,_) -> a' = a)) ->
                                                // the check here relates to the special case as described in the FilterClause below.
                                                // need to make sure the pre-tuple alias (if applicable) is not used in the projection,
                                                // but rather the later alias of the same object after it has been tupled.
                                                  { q with UltimateChild = Some(legaliseName entityIndex.[0], e) }
                                        | _ -> { q with UltimateChild = Some(legaliseName a,e) }
                | SelectMany(a,b,dat,rest) ->
                   match dat with
                   | LinkQuery(link) when link.RelDirection = RelationshipDirection.Children ->
                         convert { q with Aliases = q.Aliases.Add(legaliseName b,link.ForeignTable).Add(legaliseName a,link.PrimaryTable);
                                          Links = (legaliseName a, link, legaliseName b)  :: q.Links } rest
                   | LinkQuery(link) ->
                         convert { q with Aliases = q.Aliases.Add(legaliseName a,link.ForeignTable).Add(legaliseName b,link.PrimaryTable);
                                         Links = (legaliseName a, link, legaliseName b) :: q.Links  } rest
                   | CrossJoin(a,tbl) ->
                         convert { q with Aliases = q.Aliases.Add(legaliseName a,tbl);
                                          CrossJoins = (legaliseName a, tbl) :: q.CrossJoins } rest
                   | GroupQuery(grp) ->
                         convert { q with 
                                    Aliases = q.Aliases.Add(legaliseName a,grp.PrimaryTable).Add(legaliseName b,grp.PrimaryTable);
                                    Links = q.Links  
                                    Grouping = 
                                        let baseAlias:alias = grp.PrimaryTable.Name
                                        let f = grp.KeyColumns |> List.map (fun (al,k) -> legaliseName (match al<>"" with true -> al | false -> baseAlias), k)
                                        let s = grp.AggregateColumns |> List.map (fun (al,opKey) -> legaliseName (match al<>"" with true -> al | false -> baseAlias), opKey)
                                        (f,s)::q.Grouping
                                    Projection = match grp.Projection with Some p -> p::q.Projection | None -> q.Projection } rest
                | FilterClause(c,rest) ->  convert { q with Filters = (c)::q.Filters } rest
                | HavingClause(c,rest) ->  convert { q with HavingFilters = (c)::q.HavingFilters } rest
                | Projection(exp,rest) ->
                    convert { q with Projection = exp::q.Projection } rest
                | Distinct(rest) ->
                    if q.Distinct then failwith "distinct is applied to the entire query and can only be supplied once"
                    else convert { q with Distinct = true } rest
                | OrderBy(alias,key,desc,rest) ->
                    convert { q with Ordering = (legaliseName alias,key,desc)::q.Ordering } rest
                | Skip(amount, rest) ->
                    if q.Skip.IsSome then failwith "skip may only be specified once"
                    else convert { q with Skip = Some(amount) } rest
                | Take(amount, rest) ->
                    if q.Union.IsSome then failwith "Union and take-limit is not yet supported as SQL-syntax varies."
                    match q.Take with
                    | Some x when amount <= x || amount = 1 -> convert { q with Take = Some(amount) } rest
                    | Some x -> failwith "take may only be specified once"
                    | None -> convert { q with Take = Some(amount) } rest
                | Count(rest) ->
                    if q.Count then failwith "count may only be specified once"
                    else convert { q with Count = true } rest
                | Union(all,subquery, pars, rest) ->
                    if q.Union.IsSome then failwith "Union may only be specified once. However you can try: xs.Union(ys.Union(zs))"
                    elif q.Take.IsSome then failwith "Union and take-limit is not yet supported as SQL-syntax varies."
                    else convert { q with Union = Some(all,subquery,pars) } rest
                | AggregateOp(alias, operationWithKey, rest) ->
                    convert { q with AggregateOp = (alias, operationWithKey)::q.AggregateOp } rest
            let sq = convert (SqlQuery.Empty) exp
            sq

and internal ISqlProvider =
    /// return a new, unopened connection using the provided connection string
    abstract CreateConnection : string -> IDbConnection
    /// return a new command associated with the provided connection and command text
    abstract CreateCommand : IDbConnection * string -> IDbCommand
    /// return a new command parameter with the provided name, value and optionally type, direction and length
    abstract CreateCommandParameter : QueryParameter * obj -> IDbDataParameter
    /// This function will be called when the provider is first created and should be used
    /// to generate a cache of type mappings, and to set the three mapping function properties
    abstract CreateTypeMappings : IDbConnection -> Unit
    /// Queries the information schema and returns a list of table information
    abstract GetTables  : IDbConnection * CaseSensitivityChange -> Table list
    /// Queries table descriptions / comments for tooltip-info, table name to description
    abstract GetTableDescription  : IDbConnection * string -> string
    /// Queries the given table and returns a list of its columns
    /// this function should also populate a primary key cache for tables that
    /// have a single non-composite primary key
    abstract GetColumns : IDbConnection * Table -> ColumnLookup
    /// Queries column descriptions / comments for tooltip-info, table name, column name to description
    abstract GetColumnDescription  : IDbConnection * string * string -> string
    /// Returns the primary key column for a given table from the pre-populated cache
    /// as generated by calls to GetColumns
    abstract GetPrimaryKey : Table -> string option
    /// Returns constraint information for a given table, returning two lists of relationships, where
    /// the first are relationships where the table is the primary entity, and the second where
    /// it is the foreign entity
    abstract GetRelationships : IDbConnection * Table -> (Relationship list * Relationship list)
    /// Returns a list of stored procedure metadata
    abstract GetSprocs  : IDbConnection -> Sproc list
    /// Returns the db vendor specific SQL  query to select the top x amount of rows from
    /// the given table
    abstract GetIndividualsQueryText : Table * int -> string
    /// Returns the db vendor specific SQL query to select a single row based on the table and column name specified
    abstract GetIndividualQueryText : Table * string -> string
    /// Returns cached schema information, depending on the provider the cached schema may contain the whole database schema or only the schema for entities referenced in the current context
    abstract GetSchemaCache : unit -> SchemaCache
    /// Writes all pending database changes to database
    abstract ProcessUpdates : IDbConnection * System.Collections.Concurrent.ConcurrentDictionary<SqlEntity,DateTime> * TransactionOptions * Option<int> -> unit
    /// Asynchronously writes all pending database changes to database
    abstract ProcessUpdatesAsync : System.Data.Common.DbConnection * System.Collections.Concurrent.ConcurrentDictionary<SqlEntity,DateTime> * TransactionOptions * Option<int> -> Async<unit>
    /// Accepts a SqlQuery object and produces the SQL to execute on the server.
    /// the other parameters are the base table alias, the base table, and a dictionary containing
    /// the columns from the various table aliases that are in the SELECT projection
    abstract GenerateQueryText : SqlQuery * string * Table * Dictionary<string,ResizeArray<ProjectionParameter>> * bool * IDbConnection -> string * ResizeArray<IDbDataParameter>
    ///Builds a command representing a call to a stored procedure
    abstract ExecuteSprocCommand : IDbCommand * QueryParameter[] * QueryParameter[] *  obj[] -> ReturnValueType
    ///Builds a command representing a call to a stored procedure, executing async
    abstract ExecuteSprocCommandAsync : System.Data.Common.DbCommand * QueryParameter[] * QueryParameter[] *  obj[] -> Async<ReturnValueType>
    ///Provider specific lock to do provider specific locking
    abstract GetLockObject : unit -> obj

and internal SchemaCache =
    { PrimaryKeys   : ConcurrentDictionary<string,string list>
      Tables        : ConcurrentDictionary<string,Table>
      Columns       : ConcurrentDictionary<string,ColumnLookup>
      Relationships : ConcurrentDictionary<string,Relationship list * Relationship list>
      Sprocs        : ResizeArray<Sproc>
      SprocsParams  : ConcurrentDictionary<string,QueryParameter list> //sproc name and params
      Packages      : ResizeArray<CompileTimeSprocDefinition>
      Individuals   : ResizeArray<SqlEntity>
      IsOffline     : bool }
    with
        static member Empty = { 
            PrimaryKeys = ConcurrentDictionary<string,string list>()
            Tables = ConcurrentDictionary<string,Table>()
            Columns = ConcurrentDictionary<string,ColumnLookup>()
            Relationships = ConcurrentDictionary<string,Relationship list * Relationship list>()
            Sprocs = ResizeArray()
            SprocsParams = ConcurrentDictionary<string,QueryParameter list>()
            Packages = ResizeArray()
            Individuals = ResizeArray()
            IsOffline = false }
        static member Load(filePath) =
            use ms = new MemoryStream(Encoding.UTF8.GetBytes(File.ReadAllText(filePath)))
            let ser = Runtime.Serialization.Json.DataContractJsonSerializer(typeof<SchemaCache>)
            { (ser.ReadObject(ms) :?> SchemaCache) with IsOffline = true }
        static member LoadOrEmpty(filePath) =
            if String.IsNullOrEmpty(filePath) || (not(System.IO.File.Exists filePath)) then 
                SchemaCache.Empty
            else
                SchemaCache.Load(filePath)
        member this.Save(filePath) =
            use ms = new MemoryStream()
            let ser = Runtime.Serialization.Json.DataContractJsonSerializer(this.GetType())
            ser.WriteObject(ms, { this with IsOffline = true });  
            let json = ms.ToArray();  
            File.WriteAllText(filePath, Encoding.UTF8.GetString(json, 0, json.Length))

/// GroupResultItems is an item to create key-igrouping-structure.
/// From the select group-by projection, aggregate operations like Enumerable.Count() 
/// is replaced to GroupResultItems.AggregateCount call and this is used to fetch the 
/// SQL result instead of actually counting anything
type GroupResultItems<'key, 'SqlEntity>(keyname:String*String, keyval, distinctItem:'SqlEntity) as this =
    inherit ResizeArray<'SqlEntity> ([|distinctItem|]) 
    new(keyname, keyval, distinctItem:'SqlEntity) = GroupResultItems((keyname,""), keyval, distinctItem)
    member private __.FetchItem<'ret> itemType (columnName:Option<string>) =
        let fetchCol =
            match columnName with
            | None -> fst(keyname).ToUpperInvariant()
            | Some c -> c.ToUpperInvariant()
        let itms =
            match box distinctItem with
            | :? SqlEntity ->
                let ent = unbox<SqlEntity> distinctItem
                ent.ColumnValues 
                    |> Seq.filter(fun (s,k) -> 
                        let sUp = s.ToUpperInvariant()
                        (sUp.Contains("_"+fetchCol)) && 
                            (sUp.Contains(itemType+"_")))
            | :? Tuple<SqlEntity,SqlEntity> ->
                let ent1, ent2 = unbox<SqlEntity*SqlEntity> distinctItem
                Seq.concat [| ent1.ColumnValues; ent2.ColumnValues; |]
                    |> Seq.distinct |> Seq.filter(fun (s,k) -> 
                        let sUp = s.ToUpperInvariant()
                        (sUp.Contains("_"+fetchCol)) && 
                            (sUp.Contains(itemType+"_")))
            | :? Tuple<SqlEntity,SqlEntity,SqlEntity> ->
                let ent1, ent2, ent3 = unbox<SqlEntity*SqlEntity*SqlEntity> distinctItem
                Seq.concat [| ent1.ColumnValues; ent2.ColumnValues; ent3.ColumnValues;|]
                    |> Seq.distinct |> Seq.filter(fun (s,k) -> 
                        let sUp = s.ToUpperInvariant()
                        (sUp.Contains("_"+fetchCol)) && 
                            (sUp.Contains(itemType+"_")))
            | :? Tuple<SqlEntity,SqlEntity,SqlEntity,SqlEntity> ->
                let ent1, ent2, ent3, ent4 = unbox<SqlEntity*SqlEntity*SqlEntity*SqlEntity> distinctItem
                Seq.concat [| ent1.ColumnValues; ent2.ColumnValues; ent3.ColumnValues;ent4.ColumnValues;|]
                    |> Seq.distinct |> Seq.filter(fun (s,k) -> 
                        let sUp = s.ToUpperInvariant()
                        (sUp.Contains("_"+fetchCol)) && 
                            (sUp.Contains(itemType+"_")))
            | :? Microsoft.FSharp.Linq.RuntimeHelpers.AnonymousObject<SqlEntity,SqlEntity> ->
                let ent = unbox<Microsoft.FSharp.Linq.RuntimeHelpers.AnonymousObject<SqlEntity,SqlEntity>> distinctItem
                Seq.concat [| ent.Item1.ColumnValues; ent.Item2.ColumnValues; |]
                    |> Seq.distinct |> Seq.filter(fun (s,k) -> 
                        let sUp = s.ToUpperInvariant()
                        (sUp.Contains("_"+fetchCol)) && 
                            (sUp.Contains(itemType+"_")))
            | :? Microsoft.FSharp.Linq.RuntimeHelpers.AnonymousObject<SqlEntity,SqlEntity,SqlEntity> ->
                let ent = unbox<Microsoft.FSharp.Linq.RuntimeHelpers.AnonymousObject<SqlEntity,SqlEntity,SqlEntity>> distinctItem
                Seq.concat [| ent.Item1.ColumnValues; ent.Item2.ColumnValues; ent.Item3.ColumnValues; |]
                    |> Seq.distinct |> Seq.filter(fun (s,k) -> 
                        let sUp = s.ToUpperInvariant()
                        (sUp.Contains("_"+fetchCol)) && 
                            (sUp.Contains(itemType+"_")))
            | :? Microsoft.FSharp.Linq.RuntimeHelpers.AnonymousObject<SqlEntity,SqlEntity,SqlEntity,SqlEntity> ->
                let ent = unbox<Microsoft.FSharp.Linq.RuntimeHelpers.AnonymousObject<SqlEntity,SqlEntity,SqlEntity,SqlEntity>> distinctItem
                Seq.concat [| ent.Item1.ColumnValues; ent.Item2.ColumnValues; ent.Item3.ColumnValues; ent.Item4.ColumnValues; |]
                    |> Seq.distinct |> Seq.filter(fun (s,k) -> 
                        let sUp = s.ToUpperInvariant()
                        (sUp.Contains("_"+fetchCol)) && 
                            (sUp.Contains(itemType+"_")))
            | _ -> failwith ("Unknown aggregate item: " + typeof<'SqlEntity>.Name)
        let itm = 
            if Seq.isEmpty itms then 
                failwithf "Unsupported aggregate: %s %s %s" (fst keyname) (snd keyname) (if columnName.IsSome then columnName.Value else "")
            else itms |> Seq.head |> snd
        if itm = box(DBNull.Value) then Unchecked.defaultof<'ret>
        else 
            let returnType = typeof<'ret>
            Utilities.convertTypes itm returnType :?> 'ret
    member __.Values = [|distinctItem|]
    interface System.Linq.IGrouping<'key, 'SqlEntity> with
        member __.Key = keyval
    member __.AggregateCount<'T>(columnName) = this.FetchItem<'T> "COUNT" columnName
    member __.AggregateSum<'T>(columnName) = 
        let x = this.FetchItem<'T> "SUM" columnName 
        x
    member __.AggregateAverage<'T>(columnName) = this.FetchItem<'T> "AVG" columnName
    member __.AggregateAvg<'T>(columnName) = this.FetchItem<'T> "AVG" columnName
    member __.AggregateMin<'T>(columnName) = this.FetchItem<'T> "MIN" columnName
    member __.AggregateMax<'T>(columnName) = this.FetchItem<'T> "MAX" columnName
    member __.AggregateStandardDeviation<'T>(columnName) = this.FetchItem<'T> "STDDEV" columnName
    member __.AggregateStDev<'T>(columnName) = this.FetchItem<'T> "STDDEV" columnName
    member __.AggregateStdDev<'T>(columnName) = this.FetchItem<'T> "STDDEV" columnName
    member __.AggregateVariance<'T>(columnName) = this.FetchItem<'T> "VAR" columnName
    static member op_Implicit(x:GroupResultItems<'key, 'SqlEntity>) : Microsoft.FSharp.Linq.RuntimeHelpers.Grouping<'key, 'SqlEntity> =
        Microsoft.FSharp.Linq.RuntimeHelpers.Grouping((x :> System.Linq.IGrouping<_,_>).Key, x.Values)
    static member op_Implicit(x:Microsoft.FSharp.Linq.RuntimeHelpers.Grouping<'key, 'SqlEntity>) : GroupResultItems<'key, 'SqlEntity> =
        let key = x.GetType().GetField("key", BindingFlags.NonPublic ||| BindingFlags.Instance)
        let v = key.GetValue(x) |> unbox<'key>
        let i = x |> Seq.head
        GroupResultItems<'key, 'SqlEntity>("", v, i)

module internal CommonTasks =

    let ``ensure columns have been loaded`` (provider:ISqlProvider) (con:IDbConnection) (entities:ConcurrentDictionary<SqlEntity, DateTime>) =
        entities |> Seq.map(fun e -> e.Key.Table)
                    |> Seq.distinct
                    |> Seq.iter(fun t -> provider.GetColumns(con,t) |> ignore )

    let checkKey (pkLookup:ConcurrentDictionary<string, string list>) id (e:SqlEntity) =
        if pkLookup.ContainsKey e.Table.FullName then
            match e.GetPkColumnOption pkLookup.[e.Table.FullName] with
            | [] ->  e.SetPkColumnSilent(pkLookup.[e.Table.FullName], id)
            | _  -> () // if the primary key exists, do nothing
                            // this is because non-identity columns will have been set
                            // manually and in that case scope_identity would bring back 0 "" or whatever

    let parseHaving fieldNotation (keys:(alias*SqlColumnType) list) (conditionList : Condition list) =
        if keys.Length <> 1 then
            failwithf "Unsupported having. Expected 1 key column, found: %i" keys.Length
        else
            let basealias, key = keys.[0]
            let replaceAlias = function "" -> basealias | x -> x
            let replaceEmptyKey = 
                match key with
                | KeyColumn keyName -> function GroupColumn (KeyOp k,c) when k = "" -> GroupColumn (KeyOp keyName,c) | x -> x
                | _ -> id

            let rec parseFilters conditionList = 
                conditionList |> List.map(function 
                    | And(curr, tail) -> 
                        let converted = curr |> List.map (fun (alias,c,op,i) -> replaceAlias alias, replaceEmptyKey c, op, i)
                        And(converted, tail |> Option.map parseFilters)
                    | Or(curr,tail) -> 
                        let converted = curr |> List.map (fun (alias,c,op,i) -> replaceAlias alias, replaceEmptyKey c, op, i)
                        Or(curr, tail |> Option.map parseFilters)
                    | x -> x)
            parseFilters conditionList

module public OfflineTools =

    /// Merges two ContexSchemaPath offline schema files into one target schema file.
    /// This is a tool method that can be useful in multi-project solution using the same database with different tables.
    let mergeCacheFiles(sourcefile1, sourcefile2, targetfile) =
        if not(System.IO.File.Exists sourcefile1) then "File not found: " + sourcefile1
        elif not(System.IO.File.Exists sourcefile2) then "File not found: " + sourcefile2
        else
        if System.IO.File.Exists targetfile then
            System.IO.File.Delete targetfile
        let s1 = SchemaCache.Load sourcefile1
        let s2 = SchemaCache.Load sourcefile2
        let merged = 
            {   PrimaryKeys = System.Collections.Concurrent.ConcurrentDictionary( 
                                Seq.concat [|s1.PrimaryKeys ; s2.PrimaryKeys |] |> Seq.distinctBy(fun d -> d.Key));
                Tables = System.Collections.Concurrent.ConcurrentDictionary( 
                                Seq.concat [|s1.Tables ; s2.Tables |] |> Seq.distinctBy(fun d -> d.Key));
                Columns = System.Collections.Concurrent.ConcurrentDictionary( 
                                Seq.concat [|s1.Columns ; s2.Columns |] |> Seq.distinctBy(fun d -> d.Key));
                Relationships = System.Collections.Concurrent.ConcurrentDictionary( 
                                    Seq.concat [|s1.Relationships ; s2.Relationships |] |> Seq.distinctBy(fun d -> d.Key));
                Sprocs = ResizeArray(Seq.concat [| s1.Sprocs ; s2.Sprocs |] |> Seq.distinctBy(fun s ->
                                        let rec getName = 
                                            function
                                            | Root(name, sp) -> name + "_" + (getName sp)
                                            | Package(n, ctpd) -> n + "_" + ctpd.ToString()
                                            | Sproc ctpd -> ctpd.ToString()
                                            | Empty -> ""
                                        getName s));
                SprocsParams = System.Collections.Concurrent.ConcurrentDictionary( 
                                Seq.concat [|s1.SprocsParams ; s2.SprocsParams |] |> Seq.distinctBy(fun d -> d.Key));
                Packages = ResizeArray(Seq.concat [| s1.Packages ; s2.Packages |] |> Seq.distinctBy(fun s -> s.ToString()));
                Individuals = ResizeArray(Seq.concat [| s1.Individuals ; s2.Individuals |] |> Seq.distinct);
                IsOffline = s1.IsOffline || s2.IsOffline}
        merged.Save targetfile
        "Merge saved " + targetfile + " at " + DateTime.Now.ToString("hh:mm:ss")