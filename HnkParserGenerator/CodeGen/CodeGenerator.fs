module internal HnkParserGenerator.CodeGen.CodeGenerator

open HnkParserGenerator.LALR
open HnkParserGenerator
open HnkParserGenerator.CodeGen.Code
open System.IO

// TODO partial ParseTree with errors instead of reducer

type private Ident =
    | Ident of string
    override this.ToString() = let (Ident str) = this in str

type private Type =
    | Type of string
    override this.ToString() = let (Type str) = this in str

let private comment str = Line $"// %s{str}"

let private blankLine = Line ""

let private header = code {
    comment "---------------------------------------------------------------------"
    comment "This code was generated by a tool."
    comment "Changes to this file may cause incorrect behavior and will be lost if"
    comment "the code is regenerated."
    comment "---------------------------------------------------------------------"
}

let private failwithInvalidState = "failwithInvalidState ()"

let private terminalType = Type "Terminal"
let private reducerType = Type "Reducer"
let private parseErrorLookaheadType = Type "ParseErrorLookahead"
let private parseErrorType = Type "ParseError"
let private loggerType = Type "Logger"

let private reducerParamName = Ident "reducer"
let private loggerParamName = Ident "logger"
let private inputParamName = Ident "input"

let private inputEnumeratorVarName = Ident "inputEnumerator"
let private lhsStackVarName = Ident "lhsStack"
let private stateStackVarName = Ident "stateStack"
let private resultVarName = Ident "result"
let private acceptedVarName = Ident "accepted"
let private lookaheadVarName = Ident "lookahead"
let private lookaheadIsEofVarName = Ident "lookaheadIsEof"
let private keepGoingVarName = Ident "keepGoing"
let private reductionResultVarName = Ident "reduced"

let private loggerMemberShiftedName = Ident "LogShifted"
let private loggerMemberShiftedEofLookaheadName = Ident "LogShiftedEofLookahead"
let private loggerMemberAcceptedName = Ident "LogAccepted"
let private loggerMemberErrorName = Ident "LogError"

let private loggerStaticMembers =
    [ loggerMemberShiftedName, Type $"terminal: {terminalType} * lookahead: {terminalType} -> unit"
      loggerMemberShiftedEofLookaheadName, Type $"terminal: {terminalType} -> unit"
      loggerMemberAcceptedName, Type "unit -> unit"
      loggerMemberErrorName, Type "unit -> unit" ]

let private parseErrorLookaheadCases =
    [ Ident "Terminal", Some terminalType
      Ident "Eof", None ]

let private parseErrorRecordFields =
    [ Ident "lookahead", parseErrorLookaheadType
      Ident "leftHandSideStack", Type "obj list"
      Ident "stateStack", Type "int list" ]

let private unitType = Type (nameof(unit))

type private Context<'s when 's : comparison> =
    { eof : 's
      resultType : Type
      toIdent : 's -> Ident
      getType : 's -> Type
      getNum : State<'s> -> int
      startingStateNum : int
      productions : Production<'s> list
      states : State<'s> list
      terminalCases : (Ident * Type option) list
      reducerFields : (Ident * Type) list
      loggerReducedMembers : (Ident * Type) list
      getLoggerReducedMember : Type -> Ident * Type
      gotoTable : (State<'s> * 's * State<'s>) list
      actionTable : (State<'s> * ('s * Action<'s>) list) list }

let private moduleDecl (name : Ident) = Line $"module internal {name}"

let private sumTypeDecl (typeName : Type) (cases : #seq<Ident * Type option>) = code {
    Line $"type {typeName} ="
    Indented <| code {
        for name, type_ in cases do
            match type_ with
            | None -> Line $"| {name}"
            | Some (Type t) ->
                let t =
                    if t.Contains('*')
                    then $"({t})"
                    else t
                Line $"| {name} of {t}"
    }
}

let private recordDecl (typeName : Type) (fields : #seq<Ident * Type>) = code {
    let fieldLine (name, type_) = Line $"{name} : {type_}"

    Line $"type {typeName} = {{"
    Indented (Block (fields |> Seq.map fieldLine |> List.ofSeq))
    Line "}"
}

let private interfaceDecl (typeName : Type) (members : #seq<Ident * Type>) = code {
    Line $"type {typeName} ="
    Indented <| code {
        for memberName, memberType in members do
            Line $"abstract member {memberName}: {memberType}"
    }
}

let private symbolToTerminalCase toIdent s = $"T_{toIdent s}"

let private productionToReducerFieldName toIdent production =
    (toIdent production.from).ToString() +
        "_" +
        (production.into
        |> Seq.map (toIdent >> string)
        |> String.concat "_")
    |> Ident

let private shift ctx lookahead newState =
    let caseName = symbolToTerminalCase ctx.toIdent lookahead
    let lookaheadHasPayload = ctx.getType lookahead <> unitType
    code {
        if lookaheadHasPayload
        then Line $"| {caseName} x ->"
        else Line $"| {caseName} ->"
        Indented <| code {
            comment "shift"
            if lookaheadHasPayload then
                Line $"{lhsStackVarName}.Push(x)"
            Line $"if {inputEnumeratorVarName}.MoveNext() then"
            Indented <| code {
                Line $"{loggerParamName}.{loggerMemberShiftedName}({lookaheadVarName}, {inputEnumeratorVarName}.Current)"
                Line $"{lookaheadVarName} <- {inputEnumeratorVarName}.Current"
            }
            Line "else"
            Indented <| code {
                Line $"{loggerParamName}.{loggerMemberShiftedEofLookaheadName}({lookaheadVarName})"
                Line $"{lookaheadIsEofVarName} <- true"
            }
            Line $"{stateStackVarName}.Push({ctx.getNum newState})"
        }
    }

let private applyReduction ctx production =
    let args =
        production.into
        |> Seq.map ctx.getType
        |> Seq.filter (fun t -> t <> unitType)
        |> Seq.mapi (fun i t -> ($"arg{i + 1}", t) )
        |> List.ofSeq

    let argListStr =
        args
        |> Seq.map fst
        |> String.concat ", "

    let reducerField = productionToReducerFieldName ctx.toIdent production

    code {
        for _ = 1 to production.into.Length do
            Line $"{stateStackVarName}.Pop() |> ignore"
        for argName, argType in args |> List.rev do
            Line $"let {argName} = {lhsStackVarName}.Pop() :?> {argType}"

        match args.Length with
        | 0 -> Line $"let {reductionResultVarName} = {reducerParamName}.{reducerField}"
        | 1 -> Line $"let {reductionResultVarName} = {reducerParamName}.{reducerField} {argListStr}"
        | _ -> Line $"let {reductionResultVarName} = {reducerParamName}.{reducerField} ({argListStr})"
    }

let private reduce ctx lookahead production =
    let goto =
        ctx.gotoTable
        |> List.choose (fun (src, nonTerminal, dst) ->
            if nonTerminal = production.from
            then Some (src, dst)
            else None)

    let lookaheadHasPayload = ctx.getType lookahead <> unitType

    let loggerReducedMemberName, _ = ctx.getLoggerReducedMember (ctx.getType production.from)

    code {
        if lookahead = ctx.eof then Line $"| _ when {lookaheadIsEofVarName} ->"
        elif lookaheadHasPayload then Line $"| {symbolToTerminalCase ctx.toIdent lookahead} _ ->"
        else Line $"| {symbolToTerminalCase ctx.toIdent lookahead} ->"

        Indented <| code {
            comment "reduce"
            applyReduction ctx production
            Line $"{lhsStackVarName}.Push({reductionResultVarName})"
            Line "let nextState ="
            Indented <| code {
                Line $"match {stateStackVarName}.Peek() with"
                for src, dest in goto do
                    Line $"| {ctx.getNum src} -> {ctx.getNum dest}"
                Line $"| _ -> {failwithInvalidState}"
            }
            Line $"{stateStackVarName}.Push(nextState)"
            Line $"{loggerParamName}.{loggerReducedMemberName}({reductionResultVarName})"
        }
    }

let private accept ctx production =
    code {
        Line $"| _ when {lookaheadIsEofVarName} ->"
        Indented <| code {
            comment "accept"
            applyReduction ctx production
            Line $"{resultVarName} <- {reductionResultVarName}"
            Line $"{acceptedVarName} <- true"
            Line $"{keepGoingVarName} <- false"
            Line $"{loggerParamName}.{loggerMemberAcceptedName}()"
        }
    }

let private parseFunction ctx = code {
    Line $"let parse ({reducerParamName} : {reducerType}) ({loggerParamName} : {loggerType}) ({inputParamName} : {terminalType} seq) : Result<{ctx.resultType}, {parseErrorType}> ="
    Indented <| code {
        Line $"use {inputEnumeratorVarName} = {inputParamName}.GetEnumerator()"
        Line $"let {lhsStackVarName} = System.Collections.Stack(50)"
        Line $"let {stateStackVarName} = System.Collections.Generic.Stack<int>(50)"
        Line $"let mutable {resultVarName} = Unchecked.defaultof<{ctx.resultType}>"
        Line $"let mutable {acceptedVarName} = false"
        blankLine
        Line $"{stateStackVarName}.Push({ctx.startingStateNum})"
        blankLine
        Line $"let mutable {lookaheadVarName}, {lookaheadIsEofVarName} ="
        Indented <| code {
            Line $"if {inputEnumeratorVarName}.MoveNext()"
            Line $"then ({inputEnumeratorVarName}.Current, false)"
            Line $"else (Unchecked.defaultof<{terminalType}>, true)"
        }
        blankLine
        Line $"let mutable {keepGoingVarName} = true"
        Line $"while {keepGoingVarName} do"
        Indented <| code {
            Line $"match {stateStackVarName}.Peek() with"
            for state, stateActions in ctx.actionTable do
                Line $"| {ctx.getNum state} ->"
                Indented <| code {
                    Line $"match {lookaheadVarName} with"

                    let stateActions = stateActions |> List.sortBy (fun (s, _) -> if s = ctx.eof then 0 else 1)

                    for lookahead, action in stateActions do
                        match action with
                        | Shift newState -> shift ctx lookahead newState
                        | Reduce production -> reduce ctx lookahead production
                        | Accept production -> accept ctx production

                    Line "| _ ->"
                    Indented <| code {
                        comment "error"
                        Line $"{keepGoingVarName} <- false"
                        Line $"{loggerParamName}.{loggerMemberErrorName}()"
                    }
                }
            Line $"| _ -> {failwithInvalidState}"
        }
        blankLine
        Line $"if {acceptedVarName}"
        Line $"then Ok {resultVarName}"
        Line "else Error {"
        Indented <| code {
            Line $"lookahead = if {lookaheadIsEofVarName} then Eof else Terminal {lookaheadVarName}"
            Line $"leftHandSideStack = {lhsStackVarName}.ToArray() |> List.ofArray"
            Line $"stateStack = {stateStackVarName} |> List.ofSeq"
        }
        Line "}"
    }
}

type CodeGenArgs<'s when 's : comparison> =
    { newLine : string
      eofSymbol : 's
      symbolTypes : DefaultingMap<'s, string>
      symbolToIdentifier : 's -> string
      parsingTable : ParsingTable<'s>
      parserModuleName : string }

let private createContext args =
    let toIdent = args.symbolToIdentifier >> Ident

    let getType s = args.symbolTypes |> DefaultingMap.find s |> Type

    let resultType =
        args.parsingTable.grammar.startingSymbol
        |> getType

    let states =
        args.parsingTable.action
        |> Map.toSeq
        |> Seq.map fst
        |> List.ofSeq

    let getNum s = args.parsingTable.stateNumber |> Map.find s

    let startingStateNum =
        states
        |> Seq.find (fun state ->
            state.configurations
            |> Seq.exists (fun cfg ->
                cfg.production.from = args.parsingTable.grammar.startingSymbol &&
                cfg |> Configuration.isStarting))
        |> getNum

    let productions =
        args.parsingTable.grammar.productions
        |> List.ofSeq

    let terminalCases =
        args.parsingTable.grammar.terminals
        |> Seq.map (fun t ->
            let name = symbolToTerminalCase toIdent t |> Ident
            let type_ = getType t
            let type_ =
                if type_ = unitType
                then None
                else Some type_
            (name, type_))
        |> Seq.sort
        |> List.ofSeq

    let reducerFields =
        args.parsingTable.grammar.productions
        |> Seq.map (fun p ->
            let name = productionToReducerFieldName toIdent p

            let type_ =
                let resultType = getType p.from

                let argTypes =
                    p.into
                    |> List.choose (fun s ->
                        match getType s with
                        | t when t = unitType -> None
                        | t -> Some t)

                let argTypeStrings =
                    argTypes
                    |> List.map string

                let argTypeStrings =
                    if argTypeStrings.Length > 1 then
                        argTypeStrings
                        |> List.map (fun tstr ->
                            if tstr.Contains('*')
                            then $"({tstr})"
                            else tstr)
                    else
                        argTypeStrings

                let argTypeString =
                    argTypeStrings
                    |> String.concat " * "

                if argTypeStrings.Length > 0
                then Type $"{argTypeString} -> {resultType}"
                else Type $"{resultType}"

            (name, type_))
        |> Seq.sortBy fst
        |> List.ofSeq

    let loggerReduceMembersWithNonTerminals =
        args.parsingTable.grammar.productions
        |> Seq.map (fun p ->
            let nonTerminalType = getType p.from
            let memberName = Ident $"LogReduced{toIdent p.from}"
            let memberType = Type $"nonTerminal: {nonTerminalType} -> unit"
            (nonTerminalType, (memberName, memberType)))
        |> Seq.distinct
        |> List.ofSeq

    let loggerReduceMembers =
        loggerReduceMembersWithNonTerminals
        |> List.map snd
    let getLoggerReducedMember type_ =
        loggerReduceMembersWithNonTerminals
        |> Seq.find (fun (t, _) -> t = type_)
        |> snd

    let gotoTable =
        args.parsingTable.goto
        |> Map.toSeq
        |> Seq.collect (fun (src, stateGoto) ->
            stateGoto
            |> Map.toSeq
            |> Seq.map (fun (symbol, dest) ->
                (src, symbol, dest)))
        |> List.ofSeq

    let actionTable =
        args.parsingTable.action
        |> Map.toSeq
        |> Seq.map (fun (state, stateActions) ->
            let actions =
                stateActions
                |> Map.toSeq
                |> List.ofSeq
            (state, actions))
        |> List.ofSeq

    { eof = args.eofSymbol
      resultType = resultType
      toIdent = toIdent
      getType = getType
      getNum = getNum
      startingStateNum = startingStateNum
      productions = productions
      states = states
      terminalCases = terminalCases
      reducerFields = reducerFields
      loggerReducedMembers = loggerReduceMembers
      getLoggerReducedMember = getLoggerReducedMember
      gotoTable = gotoTable
      actionTable = actionTable }

let generate (args : CodeGenArgs<'s>) (stream : Stream) : unit =
    let writer = new StreamWriter(stream)

    let ctx = createContext args

    let parserCode =
        code {
            header
            moduleDecl (Ident args.parserModuleName)
            blankLine
            Line "(*"
            blankLine
            Line (string args.parsingTable)
            Line "*)"
            blankLine
            sumTypeDecl terminalType ctx.terminalCases
            blankLine
            recordDecl reducerType ctx.reducerFields
            blankLine
            interfaceDecl loggerType (loggerStaticMembers @ ctx.loggerReducedMembers)
            blankLine
            sumTypeDecl parseErrorLookaheadType parseErrorLookaheadCases
            blankLine
            recordDecl parseErrorType parseErrorRecordFields
            blankLine
            Line "let private failwithInvalidState () = failwith \"Parser is in an invalid state. This is a bug in the parser generator.\""
            blankLine
            parseFunction ctx
        }

    parserCode |> write args.newLine writer

    writer.Flush()