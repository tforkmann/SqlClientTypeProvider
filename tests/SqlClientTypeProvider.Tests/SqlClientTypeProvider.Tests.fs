module SqlClientTypeProvider.Test
open System
open SqlClientTypeProvider

[<Literal>] 
let connStr = "Data Source=localhost; Initial Catalog=HR; Integrated Security=True"

type HR = SqlDataProvider<Common.DatabaseProviderTypes.MSSQLSERVER, connStr>

[<EntryPoint>]
let main argv =
    let runtimeConnectionString = connStr
    let ctx = HR.GetDataContext runtimeConnectionString
    let employeesFirstName = 
        query {
            for emp in ctx.Dbo.Employees do
            select emp.FirstName
        } |> Seq.head

    printfn "Hello %s!" employeesFirstName   
    System.Threading.Thread.Sleep 2000
    0 // return an integer exit code   