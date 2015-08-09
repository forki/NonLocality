module NonLocality

open System.Collections.ObjectModel
open System.Reactive.Linq
open System.Windows
open System.Windows.Controls
open System

open Amazon.S3
open FsXaml
open FSharp.Qualia
open FSharp.Qualia.WPF
open System.Threading
open MahApps.Metro.Controls

let cast<'a> (x:obj) : 'a option =
    match x with
    | :? 'a as y -> Some y
    | _ -> None

type Events = DoSync | Fetch | Remove | SelectionChanged of FileSyncPreview option

type SyncWindow = XAML<"SyncWindow.xaml", true>
type SyncItem = XAML<"SyncItem.xaml", true>

type SyncItemView(m, elt:SyncItem) =
    inherit View<Events, FrameworkElement, FileSyncPreview>(elt.Root, m)
    let {file=file;action=action} = m
    override x.EventStreams = []
    override x.SetBindings m =
        elt.name.Content <- file.key
        elt.status.Content <- sprintf "%A" file.status
        match action with
        | NoAction -> elt.actionSkip.IsChecked <- Nullable true
        | GetRemote -> elt.actionGet.IsChecked <- Nullable true
        | SendLocal -> elt.actionSend.IsChecked <- Nullable true
        | ResolveConflict -> failwith "Not implemented yet"

//        elt.action.Content <- sprintf "%A" action

type SyncModel() =
    member val Items = new ObservableCollection<FileSyncPreview>()
    member val SelectedItem = new ReactiveProperty<FileSyncPreview option>(None)
    member val Refresh = new ReactiveProperty<Unit>(())

type SyncView(elt:SyncWindow, m) =
    inherit DerivedCollectionSourceView<Events, MetroWindow, SyncModel>(elt.Root, m)

    do
        elt.buttonCancel.Click.Add (fun _ -> elt.Root.Close())

    override x.EventStreams = [
        elt.buttonSync.Click --> DoSync
        elt.button.Click --> Fetch
        elt.list.SelectionChanged |> Observable.map (fun _ -> SelectionChanged((cast<SyncItemView> elt.list.SelectedItem |> Option.map(fun v -> v.Model))))
        elt.list.KeyDown |> Observable.filter (fun (e:Input.KeyEventArgs) -> e.Key = Input.Key.Delete) |> Observable.mapTo Remove
        Observable.Return Fetch ]
    override x.SetBindings m =
        let collview = x.linkCollection elt.list (fun i -> SyncItemView(i, SyncItem())) m.Items
        m.SelectedItem |> Observable.add (fun i -> elt.label.Content <- sprintf "Press <DEL> to delete the selection item. Current Selection: %A" i)
        ()

type SyncController(s3:IAmazonS3, sp:SyncPoint) =
    let fetch (m:SyncModel) =
        async {
            do m.Items.Clear()
            let! (_,_,files) = sp |> SyncPoint.fetch s3 true
            let syncPreview = files |> SyncPoint.syncPreview s3 sp
            do syncPreview |> Array.iter (m.Items.Add)
        }
    member x.doSync (m:SyncModel) =
        async {
            do! SyncPoint.doSync s3 sp (Array.ofSeq m.Items) |> Async.Ignore
            do! fetch m
        }
    interface IDispatcher<Events,SyncModel> with
        member x.InitModel m = ()
        member x.Dispatcher = 
            function
            | Fetch -> Async fetch
            | Remove -> Sync (fun m -> m.SelectedItem.Value |> Option.iter (m.Items.Remove >> ignore))
            | SelectionChanged item -> printfn "%A" item; Sync (fun m -> m.SelectedItem.Value <- item)
            | DoSync -> Async x.doSync

type App = XAML<"App.xaml">

[<EntryPoint>]
[<STAThread>]
let main args =
    let p = NonLocality.Lib.Profiles.getProfile()
    if Option.isNone p then 1
    else
        let pp = p.Value
        let s3 = NonLocality.Lib.Profiles.createClient pp
//        let buckets = SyncPoint.listBuckets s3
        let sp = SyncPoint.load "..\\..\\sp.json"
        tracefn "%A" sp
//        let sp = SyncPoint.create "sync-bucket-test" "F:\\tmp\\nonlocality"  [||] SyncTrigger.Manual
//        let sp = { sp with rules = [| Rule.fromPattern "\\*\\.jpg" (Number 1) |] }
//        SyncPoint.save "..\\..\\sp.json" sp
//        let sp = SyncPoint.create "sync-bucket-test" "F:\\tmp\\nonlocality"  [||] SyncTrigger.Manual
//        let json = JsonConvert.SerializeObject(sp, Formatting.Indented)
//        do System.IO.File.WriteAllText(path, json)
        let app = App()
        let lm = SyncModel()
        let v = SyncView(new SyncWindow(),lm)
        let c = SyncController(s3, sp)
        let loop = EventLoop(v, c)
//        use l = loop.Start()
        WpfApp.runApp loop v app.Root

