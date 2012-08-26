﻿namespace Vosen.Juiz

module FunkSVD =

    open System.Collections.Generic
    open System.Threading.Tasks
    type pair<'a,'b> = System.Collections.Generic.KeyValuePair<'a,'b>

    [<assembly: System.Runtime.CompilerServices.InternalsVisibleTo("FunkSVD.Tests")>]
    do()

    let defaultFeature = 0.1
    let learningRate = 0.001
    let epochs = 75
    let regularization = 0.015
    let minimumImprovement = 0.0001
    let minimumPredictImprovement = 0.001

    type Rating =
        struct
            val mutable Title : int
            val mutable User : int
            val mutable Score : float
            new(title, user, score) = { Title = title; User = user; Score = score }
        end

    type RatingCache =
        struct
            val Rating : Rating
            val mutable Estimate : float
            new(rating, estimate) = { Rating = rating; Estimate = estimate }
        end

    type Estimates = { Predicted : float array; MovieCount : int; UserCount : int }

    type Dictionary<'TKey, 'TValue> with
        member this.AddOrSet addFunc setFunc (key : 'TKey)=
            let mutable tempValue = Unchecked.defaultof<'TValue>
            if this.TryGetValue(key, &tempValue) then
                this.[key] <- setFunc tempValue
            else
                this.[key] <- addFunc()

    type FactorizationProgress(count: int) =
        let mutable iterations = 0
        let progress = new Event<int * int>()
        member this.Progress = progress.Publish
        member this.PostProgress() =
            iterations <- iterations + 1
            progress.Trigger (iterations, count)

    let initializeFeatures titleCount userCount featCount =
        let titleFeatures = Array.init titleCount (fun idx -> Array.init featCount (fun _ -> defaultFeature))
        let userFeatures = Array.init userCount (fun idx -> Array.init featCount (fun _ -> defaultFeature))
        (titleFeatures, userFeatures)

    let clamp x = max 1.0 (min x 10.0)

    let copyBaseline (data : Rating array) =
        let movies = HashSet<int>()
        let users = HashSet<int>()
        for rating in data do
            movies.Add(rating.Title) |> ignore
            users.Add(rating.User) |> ignore
        { Predicted = (data |> Array.map (fun rating -> rating.Score)); MovieCount = movies.Count; UserCount = users.Count }

    let constantBaseline x (data : Rating array) =
        let copied = copyBaseline data
        { Predicted = Array.init data.Length (fun _ -> x) ; MovieCount = copied.MovieCount; UserCount = copied.UserCount }

    let userDeviation (movieAverages : float array) (ratings :  IList<int * float>) = 
        (Seq.sumBy (fun (movie, rating) -> movieAverages.[movie] - float(rating)) ratings) / float(ratings.Count)

    let simplePredictBaseline (data : Rating array) =
        let movies = Dictionary<int, (int * float)>()
        let users = Dictionary<int, List<int * float>>()
        for i in 0..(data.Length-1) do
            movies.AddOrSet (fun _ -> (1, data.[i].Score)) (fun (oldCount, oldSum) -> (oldCount + 1, oldSum + data.[i].Score)) data.[i].Title
            users.AddOrSet (fun _ -> List([| (data.[i].Title, data.[i].Score) |])) (fun ratingList -> ratingList.Add(data.[i].Title, data.[i].Score); ratingList) data.[i].User
        // We've got metrics loaded, now calculate movie averages
        let calculateMovieAverages (dic : Dictionary<_,_>) idx =
            let tempTuple = dic.[idx]
            snd tempTuple / float(fst tempTuple)
        let movieAverages = Array.init movies.Count (calculateMovieAverages movies)
        // now calculate user bias
        let userDeviations = 
            users
            |> Seq.map (fun userRatings -> (userDeviation movieAverages userRatings.Value))
            |> Seq.toArray
        let newRatings = Array.map (fun (rating : Rating) -> clamp (movieAverages.[rating.Title] + userDeviations.[rating.User])) data
        (movieAverages, { Predicted = newRatings; MovieCount = movies.Count; UserCount = users.Count })

    let averagesBaseline (data : Rating array) =
        let movies = Dictionary<int, (int * float)>()
        let users = Dictionary<int, List<int * float>>()
        for i in 0..(data.Length-1) do
            movies.AddOrSet (fun _ -> (1, data.[i].Score)) (fun (oldCount, oldSum) -> (oldCount + 1, oldSum + data.[i].Score)) data.[i].Title
            users.AddOrSet (fun _ -> List([| (data.[i].Title, data.[i].Score) |])) (fun ratingList -> ratingList.Add(data.[i].Title, data.[i].Score); ratingList) data.[i].User
        let calculateMovieAverages (dic : Dictionary<_,_>) idx =
            let tempTuple = dic.[idx]
            snd tempTuple / float(fst tempTuple)
        let movieAverages = Array.init movies.Count (calculateMovieAverages movies)
        let newRatings = data |> Array.map (fun rating -> movieAverages.[rating.Title])
        (movieAverages, { Predicted = newRatings; MovieCount = movies.Count; UserCount = users.Count })

    let predictRatingWithTrailing score movieFeature userFeature features feature =
        score + movieFeature * userFeature
        |> clamp
        |> (+) (float(features - feature - 1) * defaultFeature * defaultFeature)
        |> clamp

    let predictRating score movieFeature userFeature =
        clamp (score + movieFeature * userFeature)

    let trainFeature (movieFeatures : float[][]) (userFeatures : float[][]) (caches : RatingCache array) features feature =
        let mutable epoch = 0
        let mutable rmse, lastRmse = (0.0, infinity)
        while (epoch < epochs) || (rmse <= lastRmse - minimumImprovement) do
            lastRmse <- rmse
            let mutable squaredError = 0.0
            for cache in caches do
                let movieFeature = movieFeatures.[cache.Rating.Title].[feature]
                let userFeature = userFeatures.[cache.Rating.User].[feature]
                let predicted = predictRatingWithTrailing cache.Estimate movieFeature userFeature features feature
                let error = cache.Rating.Score - predicted
                squaredError <- squaredError + (error * error)
                movieFeatures.[cache.Rating.Title].[feature] <- movieFeature + (learningRate * (error * userFeature - regularization * movieFeature))
                userFeatures.[cache.Rating.User].[feature] <- userFeature + (learningRate * (error * movieFeature - regularization * userFeature))
            rmse <- sqrt(squaredError / float(caches.Length))
            epoch <- epoch + 1
        // now update estimates based on trained values
        for i = 0 to (caches.Length - 1) do
            let cache = caches.[i]
            caches.[i].Estimate <-  predictRating cache.Estimate movieFeatures.[cache.Rating.Title].[feature] userFeatures.[cache.Rating.User].[feature]

    let buildAsync (baseline : Rating array -> Estimates) (ratings : Rating array) features =
        let estimates = baseline ratings
        let movieFeatures, userFeatures = initializeFeatures estimates.MovieCount estimates.UserCount features
        let cache = (ratings, estimates.Predicted) ||> Array.map2 (fun rating estimate -> RatingCache(rating, estimate)) |> ref
        let progressMeter = FactorizationProgress(features)
        let task = Task.Factory.StartNew((fun _ ->
            for i in 0..(features-1) do
                trainFeature movieFeatures userFeatures !cache features i
                progressMeter.PostProgress()
            let featureAvgs = userFeatures |> Array.reduce (Array.map2 (+)) |> Array.map (fun x -> x / float(userFeatures.Length))
            (movieFeatures, featureAvgs)))
        (task, progressMeter)

    let saveArray (conv: 'a -> string) (data : 'a array array) =
        data |> Array.map (fun line -> String.concat "\t" (line |> Array.map conv)) |> String.concat System.Environment.NewLine

    let loadArray (conv: string ->'a) (saved : string) =
        saved.Split([| System.Environment.NewLine |], System.StringSplitOptions.None) |> Array.map (fun line -> line.Split('\t') |> Array.map conv)

    let clampedDot start x y =
        Array.fold2 (fun sum a b -> clamp(sum + a*b)) start x y

    type Model(data : float[][], featureAvgs: float array) =

        static member predictRatingWithKnown score (movieFeatures : float array) (userFeatures : float array) features feature =
            let mutable predicted = clamp(score + movieFeatures.[feature] * userFeatures.[feature])
            // add trailing
            for i = feature + 1 to features - 1 do
                predicted <- clamp (predicted + movieFeatures.[i] * userFeatures.[i])
            predicted

        static member trainFeature (data : float[][]) (userFeatures : float[]) (ratings : pair<int,float> array) (estimates : float array) features feature =
            for epoch = 0 to (epochs - 1) do
                for rating in ratings do
                    let movieFeature = data.[rating.Key].[feature]
                    let userFeature = userFeatures.[feature]
                    let predicted = Model.predictRatingWithKnown estimates.[rating.Key] data.[rating.Key] userFeatures features feature
                    let error = rating.Value - predicted
                    userFeatures.[feature] <- userFeature + (learningRate * (error * movieFeature - regularization * userFeature))
                // update estimates
            for rating in ratings do
                estimates.[rating.Key] <- predictRating estimates.[rating.Key] data.[rating.Key].[feature] userFeatures.[feature]


        static member userDeviation (movieAverages : float array) (ratings :  IList<pair<int, float>>) = 
            (ratings |> Seq.sumBy (fun rating -> movieAverages.[rating.Key] - float(rating.Value))) / float(ratings.Count)

        static member simplePredictBaseline (avgs : float array) (ratings : pair<int, float> array) =
            let deviation = Model.userDeviation avgs ratings
            avgs |> Array.map ((+) deviation)

        static member averagesBaseline (avgs : 'a array) ratings =
            avgs |> Array.copy

        member this.Features = data.[0].Length

        member this.PredictSingle (baseline : pair<int, float> array -> float array) (ratings : pair<int, float> array) target =
            let userFeatures = Array.copy featureAvgs
            let estimates = baseline ratings
            for epoch = 0 to (epochs - 1) do
                for rating in ratings do
                    let predicted = Model.predictRatingWithKnown estimates.[rating.Key] data.[rating.Key] userFeatures this.Features 0
                    let error = rating.Value - predicted
                    for feature in 0..(this.Features - 1) do
                        let movieFeature = data.[rating.Key].[feature]
                        let userFeature = userFeatures.[feature]
                        userFeatures.[feature] <- userFeature + (learningRate * (error * movieFeature - regularization * userFeature))
            // userFeatures is now features vector for this user
            clampedDot estimates.[target] data.[target] userFeatures


    type AveragedModel(data: float[][], userFeatAvgs : float array, movieAvgs : float array) =

        member this.Features = data.[0].Length

        member this.PredictUnknown(ratings : pair<int, float> array) =
            let userFeatures = Array.copy userFeatAvgs
            let estimates = Array.copy movieAvgs
            let seen = System.Collections.Generic.HashSet()
            for rating in ratings do
                seen.Add(rating.Key) |> ignore
            for feature in 0..(this.Features - 1) do
                Model.trainFeature data userFeatures ratings estimates this.Features feature
#if DEBUG
            // check the rmse
            let trained = ratings |> Array.map (fun rating -> (rating.Value, clampedDot estimates.[rating.Key] data.[rating.Key] userFeatures))
            let errorSum = trained |> Array.fold (fun error (real, predicted) -> error + ((real - predicted) * (real - predicted))) 0.0
            let rmse = sqrt(errorSum / float(trained.Length))
#endif
            // userFeatures is now features vector for this user
            for i = 0 to (estimates.Length - 1) do
                if seen.Contains(i) then
                    estimates.[i] <- 0.0
                else
                    estimates.[i] <- clampedDot estimates.[i] data.[i] userFeatures
            estimates

    type TitleRecommender(model : AveragedModel, titleToDocumentMapping: int[], documentToTitleMapping : int[]) =

        static member Load (featuresPath : string) (userFeatAvgPath : string) (movieAvgPath : string) (titleMap : string) (docMap : string) =
            let features = loadArray float (System.IO.File.ReadAllText(featuresPath))
            let movieAvgs = (loadArray float (System.IO.File.ReadAllText(movieAvgPath))).[0]
            let userFeats = (loadArray float (System.IO.File.ReadAllText(userFeatAvgPath))).[0]
            let titleMapping = (loadArray int (System.IO.File.ReadAllText(titleMap))).[0]
            let documentMapping = (loadArray int (System.IO.File.ReadAllText(docMap))).[0]
            TitleRecommender(AveragedModel(features, userFeats, movieAvgs), titleMapping, documentMapping)

        member this.PredictUnknown(ratings : pair<int, int> array) =
            ratings
            // filter out incorrect ids
            |> Array.filter (fun kvp -> this.IsCorrectId kvp.Key)
            // map ids to correct ones and normalize scores
            |> Array.map (fun kvp -> pair(titleToDocumentMapping.[kvp.Key], float(kvp.Value)))
            // send to recommender
            |> model.PredictUnknown
            // denormalize ids and scores back
            |> Array.mapi (fun docId rating -> pair(documentToTitleMapping.[docId], rating))

        member private this.IsCorrectId id =
            id >= 0 && id < titleToDocumentMapping.Length && titleToDocumentMapping.[id] >= 0

        member this.DocumentsCount 
            with get() = documentToTitleMapping.Length

        member this.TitlesCount 
            with get() = titleToDocumentMapping.Length