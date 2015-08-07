﻿namespace NonLocality.Lib

open System
open System.IO
open System.Text.RegularExpressions
open Amazon
open Amazon.Util
open Amazon.S3.Model

module Utils =
    open System.Security.Cryptography

    let Md5Hash(filename:string):string =
        use md5 = MD5.Create()
        use stream = File.OpenRead(filename)
        let bytesHash = md5.ComputeHash(stream)
        BitConverter.ToString(bytesHash).Replace("-", "").ToLower()

    let merge (a : Map<'a, 'b>) (b : Map<'a, 'b>) (f : 'a -> 'b * 'b -> 'b) =
        Map.fold (fun s k v ->
            match Map.tryFind k s with
            | Some v' -> Map.add k (f k (v, v')) s
            | None -> Map.add k v s) a b
module Profiles =

    let getProfile() =
        let l = ProfileManager.ListProfileNames() |> List.ofSeq    
        match l with
        | [] -> None
        | p :: _ -> Some (ProfileManager.GetAWSCredentials(p))
    let listProfiles() = ProfileManager.ListProfileNames()
    let createClient (p:Amazon.Runtime.AWSCredentials) =
        AWSClientFactory.CreateAmazonS3Client(p, Amazon.RegionEndpoint.USEast1)
    
module ControlledFile =
    type Status = Identical | Local | Remote
    type T = { key:string
               etag:string
               status:Status
               localModifiedDate:DateTime option
               remoteModifiedDate:DateTime option }
    let fromS3Object (o:S3Object) =
        { key = o.Key
          etag = o.ETag
          status = Remote
          localModifiedDate = None
          remoteModifiedDate = Some o.LastModified }

    let fromLocal (o:FileInfo) =
        { key = o.FullName
          etag = Utils.Md5Hash o.FullName
          status = Local
          localModifiedDate = Some o.CreationTime
          remoteModifiedDate = None }
          
    let fromLocalRemote (l:T) (r:T) =
        { key = r.key
          etag = r.etag
          status = Identical
          localModifiedDate = l.localModifiedDate
          remoteModifiedDate = r.remoteModifiedDate }
//    let fromLocalRemote (l:FileInfo) (r:S3Object) =
//        { key = r.Key
//          etag = r.ETag
//          status = Identical
//          localModifiedDate = Some l.CreationTime
//          remoteModifiedDate = Some r.LastModified }

//type SyncPoint(bucketName) =
//    member x.BucketName = bucketName

type Count = All | Zero | Number of int
type RuleSync = Latest
type Rule = { pattern : Regex
              count : Count
              sync : RuleSync }

type SyncTrigger = Manual | Periodic of TimeSpan

type FileSyncAction = NoAction | GetRemote | SendLocal | ResolveConflict
type FileSyncPreview = { file : ControlledFile.T
                         action : FileSyncAction }

module SyncPoint =
    open Newtonsoft.Json

    type T = { bucketName : string
               path : string
               rules : Rule[]
               trigger : SyncTrigger }
    type FetchResult = T * DateTime * ControlledFile.T[]
    let save path sp = 
        let json = JsonConvert.SerializeObject(sp, Formatting.Indented)
        do System.IO.File.WriteAllText(path, json)

    let load path = JsonConvert.DeserializeObject<T>(File.ReadAllText(path))

    let create bucketName path rules trigger : T = { bucketName = bucketName
                                                     path = path
                                                     rules = rules
                                                     trigger = trigger }
    let listBuckets (s3:S3.IAmazonS3) = s3.ListBuckets().Buckets
    let listLocalFiles sp =
        let d =
            if not <| Directory.Exists sp.path
            then Directory.CreateDirectory sp.path
            else new DirectoryInfo(sp.path)
        d.GetFiles() |> Array.ofSeq
    let listRemoteFiles (s3:S3.IAmazonS3) sp = 
        async {
            let! r = s3.ListObjectsAsync (S3.Model.ListObjectsRequest(BucketName = sp.bucketName)) |> Async.AwaitTask
            return r.S3Objects |> Seq.map ControlledFile.fromS3Object |> List.ofSeq
        }
    let fetch (s3:S3.IAmazonS3) sp =
        let fileKey (f:ControlledFile.T) = (f.key,f)
        async {
            let time = DateTime.Now
            let! rf = s3.ListObjectsAsync (S3.Model.ListObjectsRequest(BucketName = sp.bucketName)) |> Async.AwaitTask
            let remotes = rf.S3Objects |> Seq.map (ControlledFile.fromS3Object >> fileKey) |> Map.ofSeq
            let locals = listLocalFiles sp |> Array.map ControlledFile.fromLocal |> Array.map fileKey |> Map.ofSeq
            let files = Utils.merge remotes locals (fun k (r,l) -> ControlledFile.fromLocalRemote l r)
                        |> Map.toSeq
                        |> Seq.map snd
                        |> Seq.toArray
            return (sp,time,files)
//            return rmap
        }
    let syncPreview (s3:S3.IAmazonS3) sp (files:ControlledFile.T[]) =
        files |> Array.map (fun f -> { file = f; action = NoAction })
        