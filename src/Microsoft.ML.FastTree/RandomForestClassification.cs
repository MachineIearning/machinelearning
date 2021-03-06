// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Linq;
using Microsoft.Data.DataView;
using Microsoft.ML;
using Microsoft.ML.Calibrators;
using Microsoft.ML.CommandLine;
using Microsoft.ML.Data;
using Microsoft.ML.EntryPoints;
using Microsoft.ML.Internal.Internallearn;
using Microsoft.ML.Model;
using Microsoft.ML.Trainers.FastTree;

[assembly: LoadableClass(FastForestClassification.Summary, typeof(FastForestClassification), typeof(FastForestClassification.Options),
    new[] { typeof(SignatureBinaryClassifierTrainer), typeof(SignatureTrainer), typeof(SignatureTreeEnsembleTrainer), typeof(SignatureFeatureScorerTrainer) },
    FastForestClassification.UserNameValue,
    FastForestClassification.LoadNameValue,
    "FastForest",
    FastForestClassification.ShortName,
    "ffc")]

[assembly: LoadableClass(typeof(IPredictorProducing<float>), typeof(FastForestClassificationModelParameters), null, typeof(SignatureLoadModel),
    "FastForest Binary Executor",
    FastForestClassificationModelParameters.LoaderSignature)]

[assembly: LoadableClass(typeof(void), typeof(FastForest), null, typeof(SignatureEntryPointModule), "FastForest")]

namespace Microsoft.ML.Trainers.FastTree
{
    public abstract class FastForestOptionsBase : TreeOptions
    {
        [Argument(ArgumentType.AtMostOnce, HelpText = "Number of labels to be sampled from each leaf to make the distribtuion", ShortName = "qsc")]
        public int QuantileSampleCount = 100;

        public FastForestOptionsBase()
        {
            FeatureFraction = 0.7;
            BaggingSize = 1;
            SplitFraction = 0.7;
        }
    }

    public sealed class FastForestClassificationModelParameters :
        TreeEnsembleModelParametersBasedOnQuantileRegressionTree
    {
        internal const string LoaderSignature = "FastForestBinaryExec";
        internal const string RegistrationName = "FastForestClassificationPredictor";

        private static VersionInfo GetVersionInfo()
        {
            return new VersionInfo(
                modelSignature: "FFORE BC",
                // verWrittenCur: 0x00010001, Initial
                // verWrittenCur: 0x00010002, // InstanceWeights are part of QuantileRegression Tree to support weighted intances
                // verWrittenCur: 0x00010003, // _numFeatures serialized
                // verWrittenCur: 0x00010004, // Ini content out of predictor
                // verWrittenCur: 0x00010005, // Add _defaultValueForMissing
                verWrittenCur: 0x00010006, // Categorical splits.
                verReadableCur: 0x00010005,
                verWeCanReadBack: 0x00010001,
                loaderSignature: LoaderSignature,
                loaderAssemblyName: typeof(FastForestClassificationModelParameters).Assembly.FullName);
        }

        private protected override uint VerNumFeaturesSerialized => 0x00010003;

        private protected override uint VerDefaultValueSerialized => 0x00010005;

        private protected override uint VerCategoricalSplitSerialized => 0x00010006;

        /// <summary>
        /// The type of prediction for this trainer.
        /// </summary>
        private protected override PredictionKind PredictionKind => PredictionKind.BinaryClassification;

        internal FastForestClassificationModelParameters(IHostEnvironment env, InternalTreeEnsemble trainedEnsemble, int featureCount, string innerArgs)
            : base(env, RegistrationName, trainedEnsemble, featureCount, innerArgs)
        { }

        private FastForestClassificationModelParameters(IHostEnvironment env, ModelLoadContext ctx)
            : base(env, RegistrationName, ctx, GetVersionInfo())
        {
        }

        private protected override void SaveCore(ModelSaveContext ctx)
        {
            base.SaveCore(ctx);
            ctx.SetVersionInfo(GetVersionInfo());
        }

        private static IPredictorProducing<float> Create(IHostEnvironment env, ModelLoadContext ctx)
        {
            Contracts.CheckValue(env, nameof(env));
            env.CheckValue(ctx, nameof(ctx));
            ctx.CheckAtModel(GetVersionInfo());
            var predictor = new FastForestClassificationModelParameters(env, ctx);
            ICalibrator calibrator;
            ctx.LoadModelOrNull<ICalibrator, SignatureLoadModel>(env, out calibrator, @"Calibrator");
            if (calibrator == null)
                return predictor;
            return new SchemaBindableCalibratedModelParameters<FastForestClassificationModelParameters, ICalibrator>(env, predictor, calibrator);
        }
    }

    /// <include file='doc.xml' path='doc/members/member[@name="FastForest"]/*' />
    public sealed partial class FastForestClassification :
        RandomForestTrainerBase<FastForestClassification.Options, BinaryPredictionTransformer<FastForestClassificationModelParameters>, FastForestClassificationModelParameters>
    {
        public sealed class Options : FastForestOptionsBase
        {
            [Argument(ArgumentType.AtMostOnce, HelpText = "Upper bound on absolute value of single tree output", ShortName = "mo")]
            public Double MaxTreeOutput = 100;

            [Argument(ArgumentType.AtMostOnce, HelpText = "The calibrator kind to apply to the predictor. Specify null for no calibration", Visibility = ArgumentAttribute.VisibilityType.EntryPointsOnly)]
            internal ICalibratorTrainerFactory Calibrator = new PlattCalibratorTrainerFactory();

            [Argument(ArgumentType.AtMostOnce, HelpText = "The maximum number of examples to use when training the calibrator", Visibility = ArgumentAttribute.VisibilityType.EntryPointsOnly)]
            internal int MaxCalibrationExamples = 1000000;
        }

        internal const string LoadNameValue = "FastForestClassification";
        internal const string UserNameValue = "Fast Forest Classification";
        internal const string Summary = "Uses a random forest learner to perform binary classification.";
        internal const string ShortName = "ff";

        private bool[] _trainSetLabels;

        private protected override PredictionKind PredictionKind => PredictionKind.BinaryClassification;
        private protected override bool NeedCalibration => true;

        /// <summary>
        /// Initializes a new instance of <see cref="FastForestClassification"/>
        /// </summary>
        /// <param name="env">The private instance of <see cref="IHostEnvironment"/>.</param>
        /// <param name="labelColumn">The name of the label column.</param>
        /// <param name="featureColumn">The name of the feature column.</param>
        /// <param name="weightColumn">The name for the column containing the initial weight.</param>
        /// <param name="numLeaves">The max number of leaves in each regression tree.</param>
        /// <param name="numTrees">Total number of decision trees to create in the ensemble.</param>
        /// <param name="minDatapointsInLeaves">The minimal number of documents allowed in a leaf of a regression tree, out of the subsampled data.</param>
        internal FastForestClassification(IHostEnvironment env,
            string labelColumn = DefaultColumnNames.Label,
            string featureColumn = DefaultColumnNames.Features,
            string weightColumn = null,
            int numLeaves = Defaults.NumLeaves,
            int numTrees = Defaults.NumTrees,
            int minDatapointsInLeaves = Defaults.MinDocumentsInLeaves)
            : base(env, TrainerUtils.MakeBoolScalarLabel(labelColumn), featureColumn, weightColumn, null, numLeaves, numTrees, minDatapointsInLeaves)
        {
            Host.CheckNonEmpty(labelColumn, nameof(labelColumn));
            Host.CheckNonEmpty(featureColumn, nameof(featureColumn));
        }

        /// <summary>
        /// Initializes a new instance of <see cref="FastForestClassification"/> by using the <see cref="Options"/> class.
        /// </summary>
        /// <param name="env">The instance of <see cref="IHostEnvironment"/>.</param>
        /// <param name="options">Algorithm advanced settings.</param>
        internal FastForestClassification(IHostEnvironment env, Options options)
            : base(env, options, TrainerUtils.MakeBoolScalarLabel(options.LabelColumnName))
        {
        }

        private protected override FastForestClassificationModelParameters TrainModelCore(TrainContext context)
        {
            Host.CheckValue(context, nameof(context));
            var trainData = context.TrainingSet;
            ValidData = context.ValidationSet;
            TestData = context.TestSet;

            using (var ch = Host.Start("Training"))
            {
                ch.CheckValue(trainData, nameof(trainData));
                trainData.CheckBinaryLabel();
                trainData.CheckFeatureFloatVector();
                trainData.CheckOptFloatWeight();
                FeatureCount = trainData.Schema.Feature.Value.Type.GetValueCount();
                ConvertData(trainData);
                TrainCore(ch);
            }
            // LogitBoost is naturally calibrated to
            // output probabilities when transformed using
            // the logistic function, so if we have trained no
            // calibrator, transform the scores using that.

            // REVIEW: Need a way to signal the outside world that we prefer simple sigmoid?
            return new FastForestClassificationModelParameters(Host, TrainedEnsemble, FeatureCount, InnerOptions);
        }

        private protected override ObjectiveFunctionBase ConstructObjFunc(IChannel ch)
        {
            return new ObjectiveFunctionImpl(TrainSet, _trainSetLabels, FastTreeTrainerOptions);
        }

        private protected override void PrepareLabels(IChannel ch)
        {
            // REVIEW: Historically FastTree has this test as >= 1. TLC however
            // generally uses > 0. Consider changing FastTree to be consistent.
            _trainSetLabels = TrainSet.Ratings.Select(x => x >= 1).ToArray(TrainSet.NumDocs);
        }

        private protected override Test ConstructTestForTrainingData()
        {
            return new BinaryClassificationTest(ConstructScoreTracker(TrainSet), _trainSetLabels, 1);
        }

        private protected override BinaryPredictionTransformer<FastForestClassificationModelParameters> MakeTransformer(FastForestClassificationModelParameters model, DataViewSchema trainSchema)
         => new BinaryPredictionTransformer<FastForestClassificationModelParameters>(Host, model, trainSchema, FeatureColumn.Name);

        /// <summary>
        /// Trains a <see cref="FastForestClassification"/> using both training and validation data, returns
        /// a <see cref="BinaryPredictionTransformer{FastForestClassificationModelParameters}"/>.
        /// </summary>
        public BinaryPredictionTransformer<FastForestClassificationModelParameters> Fit(IDataView trainData, IDataView validationData)
            => TrainTransformer(trainData, validationData);

        private protected override SchemaShape.Column[] GetOutputColumnsCore(SchemaShape inputSchema)
        {
            return new[]
            {
                new SchemaShape.Column(DefaultColumnNames.Score, SchemaShape.Column.VectorKind.Scalar, NumberDataViewType.Single, false, new SchemaShape(AnnotationUtils.GetTrainerOutputAnnotation())),
                new SchemaShape.Column(DefaultColumnNames.PredictedLabel, SchemaShape.Column.VectorKind.Scalar, BooleanDataViewType.Instance, false, new SchemaShape(AnnotationUtils.GetTrainerOutputAnnotation()))
            };
        }

        private sealed class ObjectiveFunctionImpl : RandomForestObjectiveFunction
        {
            private readonly bool[] _labels;

            public ObjectiveFunctionImpl(Dataset trainSet, bool[] trainSetLabels, Options options)
                : base(trainSet, options, options.MaxTreeOutput)
            {
                _labels = trainSetLabels;
            }

            protected override void GetGradientInOneQuery(int query, int threadIndex)
            {
                int begin = Dataset.Boundaries[query];
                int end = Dataset.Boundaries[query + 1];
                for (int i = begin; i < end; ++i)
                    Gradient[i] = _labels[i] ? 1 : -1;
            }
        }
    }

    internal static partial class FastForest
    {
        [TlcModule.EntryPoint(Name = "Trainers.FastForestBinaryClassifier",
            Desc = FastForestClassification.Summary,
            UserName = FastForestClassification.UserNameValue,
            ShortName = FastForestClassification.ShortName)]
        public static CommonOutputs.BinaryClassificationOutput TrainBinary(IHostEnvironment env, FastForestClassification.Options input)
        {
            Contracts.CheckValue(env, nameof(env));
            var host = env.Register("TrainFastForest");
            host.CheckValue(input, nameof(input));
            EntryPointUtils.CheckInputArgs(host, input);

            return TrainerEntryPointsUtils.Train<FastForestClassification.Options, CommonOutputs.BinaryClassificationOutput>(host, input,
                () => new FastForestClassification(host, input),
                () => TrainerEntryPointsUtils.FindColumn(host, input.TrainingData.Schema, input.LabelColumnName),
                () => TrainerEntryPointsUtils.FindColumn(host, input.TrainingData.Schema, input.ExampleWeightColumnName),
                () => TrainerEntryPointsUtils.FindColumn(host, input.TrainingData.Schema, input.RowGroupColumnName),
                calibrator: input.Calibrator, maxCalibrationExamples: input.MaxCalibrationExamples);

        }
    }
}
