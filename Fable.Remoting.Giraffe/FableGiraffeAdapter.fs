namespace Fable.Remoting.Giraffe

open FSharp.Reflection
open Newtonsoft.Json
open Fable.Remoting.Json
open Fable.Remoting.Server
open Microsoft.AspNetCore.Http
open Giraffe.HttpHandlers
open Giraffe.Tasks

module FableGirrafeAdapter =

    open System.Text
    open System.IO

    let mutable logger : (string -> unit) option = None
    let private fableConverter = FableJsonConverter()
    let private writeLn text (sb: StringBuilder)  = sb.AppendLine(text) |> ignore; sb
    let private write  (sb: StringBuilder) text   = sb.AppendLine(text) |> ignore

    let private logDeserialization (logf: (string -> unit) option) (text: string) (inputType: System.Type) = 
        logger 
        |> Option.iter (fun log ->  
            StringBuilder()
            |> writeLn "Fable.Remoting:"
            |> writeLn "About to deserialize JSON:"
            |> writeLn text
            |> writeLn (sprintf "Into .NET Type: %s" inputType.FullName)
            |> writeLn ""
            |> fun sb -> log (sb.ToString())
        )
        

    /// Deserialize a json string using FableConverter
    let deserialize (json: string) (inputType: System.Type) =
        logDeserialization logger json inputType
        let parameterTypes = [| typeof<string>; typeof<System.Type>; typeof<JsonConverter array> |]
        let deserialize = typeof<JsonConvert>.GetMethod("DeserializeObject", parameterTypes) 
        let result = deserialize.Invoke(null, [| json; inputType; [| fableConverter |] |])
        result

        
    // serialize an object to json using FableConverter
    // json : string -> WebPart
    let json value =
      let result = JsonConvert.SerializeObject(value, fableConverter)
      StringBuilder()
      |> writeLn "Fable.Remoting: Returning serialized result back to client"
      |> writeLn result
      |> fun builder -> Option.iter (fun logf -> logf (builder.ToString())) logger
      result

    // Get data from request body and deserialize.
    // getResourceFromReq : HttpRequest -> obj
    let getResourceFromReq (ctx : HttpContext) (inputType: System.Type) =
        let requestBodyStream = ctx.Request.Body
        use streamReader = new StreamReader(requestBodyStream)
        task {
            let! requestBodyContent = streamReader.ReadToEndAsync()
            return deserialize requestBodyContent inputType
        }
        
    let handleRequest methodName serverImplementation = 
        let inputType = ServerSide.getInputType methodName serverImplementation
        let hasArg = inputType.FullName <> "Microsoft.FSharp.Core.Unit"
        fun (next : HttpFunc) (ctx : HttpContext) ->
            Option.iter (fun logf -> logf (sprintf "Fable.Remoting: Invoking method %s" methodName)) logger
            let requestBodyData = 
                match hasArg with 
                | true  -> getResourceFromReq ctx inputType
                | false -> null
            let result = ServerSide.dynamicallyInvoke methodName serverImplementation requestBodyData hasArg
            task {
                let! dynamicResult = result 
                let serializedResult = json dynamicResult
                return! text serializedResult next ctx
            }
    let webPartWithBuilderFor<'t> (implementation: 't) (routeBuilder: string -> string -> string) : HttpHandler = 
            let builder = StringBuilder()
            let typeName = implementation.GetType().Name
            write builder (sprintf "Building Routes for %s" typeName)
            implementation.GetType()
            |> FSharpType.GetRecordFields
            |> Seq.map (fun propInfo -> 
                let methodName = propInfo.Name
                let fullPath = routeBuilder typeName methodName
                write builder (sprintf "Record field %s maps to route %s" methodName fullPath)
                POST >=> route fullPath >=> handleRequest methodName implementation
            )
            |> List.ofSeq
            |> fun routes ->
                logger |> Option.iter (fun logf -> logf (builder.ToString()))
                choose routes

    let webPartFor<'t> (implementation : 't) : HttpHandler = 
        webPartWithBuilderFor implementation (sprintf "/%s/%s")