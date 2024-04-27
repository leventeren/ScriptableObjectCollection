using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using JetBrains.Annotations;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Button = UnityEngine.UIElements.Button;
using Object = UnityEngine.Object;

namespace BrunoMikoski.ScriptableObjectCollections
{
    [CustomEditor(typeof(ScriptableObjectCollection), true)]
    public class CollectionCustomEditor : Editor
    {
        private const string WAITING_FOR_SCRIPT_TO_BE_CREATED_KEY = "WaitingForScriptTobeCreated";
        private static ScriptableObject LAST_ADDED_COLLECTION_ITEM;


        [SerializeField] public VisualTreeAsset visualTreeAsset;
        [SerializeField] private VisualTreeAsset collectionItemVisualTreeAsset;


        private ListView collectionItemListView;
        private ScriptableObjectCollection collection;

        private TextField currentRenamingTextField;
        private Label currentRenamingLabel;
        private ToolbarSearchField toolbarSearchField;

        private List<ScriptableObject> filteredItems = new();

        protected virtual bool AllowCustomTypeCreation => true;
        
        
        private bool IsAutoGenerated => generatorType != null;

        [NonSerialized] private Type generatorType;
        [NonSerialized] private IScriptableObjectCollectionGeneratorBase generator;
        
        private TextField namespaceTextField;
        private Button expandShrinkButton;

        protected virtual bool CanBeReorderable
        {
            get
            {
                // If we are supposed to protect the item order, do not allow items to be reordered by dragging.
                if (collection != null && collection.ShouldProtectItemOrder)
                    return false;
                
                return true;
            }
        }

        protected virtual bool DisplayAddButton
        {
            get
            {
                // If this is a generated collection and it's set to remove items that aren't re-generated, then it
                // doesn't make sense for you to add items because they will be removed next time you generate.
                if (IsAutoGenerated && generator.ShouldRemoveNonGeneratedItems)
                    return false;
                return true;
            }
        }

        protected virtual bool DisplayRemoveButton
        {
            get
            {
                // If this is a generated collection and it's set to remove items that aren't re-generated, then it
                // doesn't make sense for you to remove items because they will be added back next time you generate.
                if (IsAutoGenerated && generator.ShouldRemoveNonGeneratedItems)
                    return false;

                // If we are supposed to protect the item order, do not allow items to be removed, otherwise you could
                // remove items from the middle and change the order.
                if (collection != null && collection.ShouldProtectItemOrder)
                    return false;
                
                return true;
            }
        }
        

        private static bool IsWaitingForNewTypeBeCreated
        {
            get => SessionState.GetBool(WAITING_FOR_SCRIPT_TO_BE_CREATED_KEY, false);
            set => SessionState.SetBool(WAITING_FOR_SCRIPT_TO_BE_CREATED_KEY, value);
        }

        public static ScriptableObject AddNewItem(ScriptableObjectCollection collection, Type itemType)
        {
            ScriptableObject collectionItem = collection.AddNew(itemType);
            Selection.objects = new Object[] {collection};
            LAST_ADDED_COLLECTION_ITEM = collectionItem;
            return collectionItem;
        }
        public override VisualElement CreateInspectorGUI()
        {
            VisualElement root = new();
            visualTreeAsset.CloneTree(root);

            collection = (ScriptableObjectCollection)target;
            collection.RefreshCollection();

            collectionItemListView = root.Q<ListView>("items-list-view");
                        
            collectionItemListView.makeItem = MakeCollectionItemListItem;
            collectionItemListView.bindItem = BindCollectionItemListItem;

            collectionItemListView.reorderable = CanBeReorderable;
            

            ReloadFilteredItems(false);
            collectionItemListView.itemsSource = filteredItems;
            

            Button addNewItemButton = root.Q<Button>("unity-list-view__add-button");
            addNewItemButton.clickable.activators.Clear();
            addNewItemButton.RegisterCallback<MouseDownEvent>(OnClickAddNewItem);
            addNewItemButton.SetEnabled(DisplayAddButton);
            
            Button removeSelectedItemsButton = root.Q<Button>("unity-list-view__remove-button");
            removeSelectedItemsButton.clickable.activators.Clear();
            removeSelectedItemsButton.RegisterCallback<MouseUpEvent>(OnClickRemoveSelectedItems);
            removeSelectedItemsButton.SetEnabled(DisplayRemoveButton);

            Button synchronizeAssetsButton = root.Q<Button>("synchronize-items-button");
            synchronizeAssetsButton.clickable.activators.Clear();
            synchronizeAssetsButton.RegisterCallback<MouseUpEvent>(OnClickToSynchronizeAssets);
            
            Button generateStaticFileButton = root.Q<Button>("generate-static-file-button");
            generateStaticFileButton.clickable.activators.Clear();
            generateStaticFileButton.RegisterCallback<MouseUpEvent>(OnClickGenerateStaticFile);
            
            
            Button generateItemsButton = root.Q<Button>("generate-auto-items");
            generateItemsButton.clickable.activators.Clear();
            generateItemsButton.RegisterCallback<MouseUpEvent>(OnClickGenerateItems);
            generateItemsButton.parent.style.display = generatorType != null ? DisplayStyle.Flex : DisplayStyle.None;


            ObjectField generatedScriptsParentFolderObjectField =
                root.Q<ObjectField>("generated-scripts-parent-folder");
            generatedScriptsParentFolderObjectField.value = SOCSettings.Instance.GetGeneratedScriptsParentFolder(collection);
            generatedScriptsParentFolderObjectField.RegisterValueChangedCallback(OnGeneratedCodeParentFolderChanged);
            
            Toggle writeAsPartialClass = root.Q<Toggle>("write-partial-class-toggle");
            writeAsPartialClass.value = SOCSettings.Instance.GetWriteAsPartialClass(collection);
            writeAsPartialClass.SetEnabled(CodeGenerationUtility.CheckIfCanBePartial(collection));
            writeAsPartialClass.RegisterValueChangedCallback(OnWriteAsPartialClassToggleChanged);
            
            Toggle useBaseClassForItems = root.Q<Toggle>("base-class-for-items-toggle");
            useBaseClassForItems.value = SOCSettings.Instance.GetUseBaseClassForITems(collection);
            useBaseClassForItems.RegisterValueChangedCallback(OnUseBaseClassForItemsToggleChanged);


            TextField staticFileNameTextField = root.Q<TextField>("static-filename-textfield");
            staticFileNameTextField.value = SOCSettings.Instance.GetStaticFilenameForCollection(collection);
            staticFileNameTextField.RegisterValueChangedCallback(OnStaticFilenameTextFieldChanged);
            

            namespaceTextField = root.Q<TextField>("namespace-textfield");
            namespaceTextField.value = SOCSettings.Instance.GetNamespaceForCollection(collection);
            namespaceTextField.RegisterValueChangedCallback(OnNamespaceTextFieldChanged);

            VisualElement extraProperties = root.Q<VisualElement>("extra-properties-visual-element");
            IMGUIContainer imguiContainer = extraProperties.Q<IMGUIContainer>();
            InspectorElement.FillDefaultInspector(imguiContainer, serializedObject, this);


            expandShrinkButton = root.Q<Button>("expand-button");
            expandShrinkButton.Add(new Image()
            {
                image = EditorGUIUtility.IconContent("d_Grid.MoveTool").image,
                scaleMode = ScaleMode.ScaleToFit
            });
            expandShrinkButton.clickable.activators.Clear();
            expandShrinkButton.RegisterCallback<MouseUpEvent>(OnToggleExpand);
            
            toolbarSearchField = root.Q<ToolbarSearchField>();
            toolbarSearchField.RegisterValueChangedCallback(OnSearchInputChanged);
            return root;
        }

        private void OnToggleExpand(MouseUpEvent evt)
        {
            bool? isOn = null;
            for (int i = 0; i < filteredItems.Count; i++)
            {
                VisualElement item = collectionItemListView.GetRootElementForIndex(i);
                
                Foldout foldout = item.Q<Foldout>();
                if (!isOn.HasValue)
                {
                    isOn = foldout.value;
                }
                
                foldout.value = !isOn.Value;
            }
        }

        private void OnEnable()
        {
            collection = (ScriptableObjectCollection)target;
            
            if (!CollectionsRegistry.Instance.IsKnowCollection(collection))
                CollectionsRegistry.Instance.ReloadCollections();
            
            // Need to cache this before the reorderable list is created, because it affects how the list is displayed.
            generatorType = CollectionGenerators.GetGeneratorTypeForCollection(collection.GetType());
            generator = generatorType == null ? null : CollectionGenerators.GetGenerator(generatorType);
            
            if (LAST_ADDED_COLLECTION_ITEM != null)
            {
                int targetIndex = collection.IndexOf(LAST_ADDED_COLLECTION_ITEM);
                RenameItemAtIndex(targetIndex);
                LAST_ADDED_COLLECTION_ITEM = null;
            }
        }
        
        private void OnNamespaceTextFieldChanged(ChangeEvent<string> evt)
        {
            string validCharsOnly = Regex.Replace(evt.newValue, @"[^A-Za-z0-9_\.]+", "_");
            string noLeadingNumbers = Regex.Replace(validCharsOnly, @"(?<=^|\.)(\d)", "_$1");
            string singleUnderscores = Regex.Replace(noLeadingNumbers, @"_+", "_");
            string trimmed = singleUnderscores.Trim('_');
            trimmed = trimmed.TrimEnd('.');
            namespaceTextField.SetValueWithoutNotify(trimmed);
            
            SOCSettings.Instance.SetNamespaceForCollection(collection, trimmed);
        }
        
        private void OnStaticFilenameTextFieldChanged(ChangeEvent<string> evt)
        {
            SOCSettings.Instance.SetStaticFilenameForCollection(collection, evt.newValue);
        }

        
        private void OnUseBaseClassForItemsToggleChanged(ChangeEvent<bool> evt)
        {
            SOCSettings.Instance.SetUsingBaseClassForItems(collection, evt.newValue);
        }
        
        private void OnWriteAsPartialClassToggleChanged(ChangeEvent<bool> evt)
        {
            SOCSettings.Instance.SetWriteAsPartialClass(collection, evt.newValue);
        }
        
        private void OnGeneratedCodeParentFolderChanged(ChangeEvent<Object> evt)
        {
            SOCSettings.Instance.SetGeneratedScriptsParentFolder(collection, evt.newValue);
        }
        
        private void OnClickToSynchronizeAssets(MouseUpEvent evt)
        {
            collection.RefreshCollection();
            ReloadFilteredItems();
        }

        private void OnClickGenerateItems(MouseUpEvent evt)
        {
            CollectionGenerators.RunGenerator(generatorType, collection);
        }
        
        private void OnClickGenerateStaticFile(MouseUpEvent evt)
        {
            CodeGenerationUtility.GenerateStaticCollectionScript(collection);
        }

        private void OnClickRemoveSelectedItems(MouseUpEvent evt)
        {
            if (!collectionItemListView.selectedIndices.Any())
            {
                RemoveItemAtIndex(filteredItems.Count - 1);
            }
            else
            {
                foreach (int selectedIndex in collectionItemListView.selectedIndices)
                {
                    RemoveItemAtIndex(selectedIndex);
                }
            }
            ReloadFilteredItems();
        }

        private void OnClickAddNewItem(MouseDownEvent evt)
        {
            List<Type> itemsSubclasses = new() { collection.GetItemType() };
            TypeCache.TypeCollection sub = TypeCache.GetTypesDerivedFrom(collection.GetItemType());
            for (int i = 0; i < sub.Count; i++)
            {
                itemsSubclasses.Add(sub[i]);
            }

            GenericMenu optionsMenu = new GenericMenu();

            for (int i = 0; i < itemsSubclasses.Count; i++)
            {
                Type itemSubClass = itemsSubclasses[i];
                if (itemSubClass.IsAbstract)
                    continue;

                optionsMenu.AddItem(new GUIContent(itemSubClass.Name), false,
                    () => { AddNewItemOfType(itemSubClass); });
            }

            optionsMenu.AddSeparator("");

            if (AllowCustomTypeCreation)
            {
                for (int i = 0; i < itemsSubclasses.Count; i++)
                {
                    Type itemSubClass = itemsSubclasses[i];

                    if (itemSubClass.IsSealed)
                        continue;

                    optionsMenu.AddItem(new GUIContent($"Create New/class $NEW : {itemSubClass.Name}"), false,
                        () => { CreateAndAddNewItemOfType(itemSubClass); });
                }
            }

            optionsMenu.ShowAsContext();
        }

        private ScriptableObject AddNewItemOfType(Type targetType, bool autoFocusForRename = true)
        {
            Undo.IncrementCurrentGroup();
            ScriptableObject newItem = collection.AddNew(targetType);
            Undo.RegisterCreatedObjectUndo(newItem, "Create New Item");
            Undo.RecordObject(collection, "Add New Item");
            Undo.SetCurrentGroupName($"Created new item {newItem.name}");
            ReloadFilteredItems();

            if (autoFocusForRename)
            {
                int newItemIndex = filteredItems.IndexOf(newItem);
                collectionItemListView.ScrollToItem(newItemIndex);
                RenameItemAtIndex(newItemIndex);
            }

            return newItem;
        }

        private void CreateAndAddNewItemOfType(Type itemSubClass)
        {
            CreateNewCollectionItemFromBaseWizard.Show(itemSubClass, success =>
            {
                if (success)
                {
                    IsWaitingForNewTypeBeCreated = true;
                }
            });
        }

        protected void RemoveItemAtIndex(int selectedIndex)
        {
            ScriptableObject scriptableObject = filteredItems[selectedIndex];
            if (scriptableObject == null)
            {
                ReloadFilteredItems();
                return;
            }
            
            Undo.RecordObject(collection, "Remove Item");

            filteredItems.Remove(scriptableObject);
            collection.Remove(scriptableObject);
            
            AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(scriptableObject));

            AssetDatabase.SaveAssetIfDirty(collection);
        }

        private void ReloadFilteredItems(bool refreshListView = true)
        {
            filteredItems.Clear();

            for (int i = 0; i < collection.Items.Count; i++)
            {
                ScriptableObject scriptableObject = collection.Items[i];
                if (scriptableObject == null)
                    continue;

                filteredItems.Add(scriptableObject);
            }

            if (refreshListView)
            {
                collectionItemListView.RefreshItems();
            }
        }
        
        private void OnSearchInputChanged(ChangeEvent<string> evt)
        {
            OnSearchValueChanged(evt.newValue);
        }

        private void OnSearchValueChanged(string targetText)
        {
            if (string.IsNullOrEmpty(targetText))
            {
                ReloadFilteredItems();
                return;
            }

            filteredItems.Clear();
            
            for (int i = 0; i < collection.Count; i++)
            {
                ScriptableObject collectionItem = collection[i];
                if (collectionItem.name.IndexOf(targetText, StringComparison.OrdinalIgnoreCase) > -1)
                {
                    filteredItems.Add(collectionItem);
                }
            }
            
            collectionItemListView.RefreshItems();
        }

        private void BindCollectionItemListItem(VisualElement targetElement, int targetIndex)
        {
            ScriptableObject targetItem = filteredItems[targetIndex];
            if (targetItem == null)
            {
                return;
            }

            IMGUIContainer imguiContainer = targetElement.Q<IMGUIContainer>("imgui-container");
            Foldout foldout = targetElement.Q<Foldout>("header-foldout");
            foldout.viewDataKey = targetIndex.ToString();
            foldout.text = targetItem.name;
            Editor editor = EditorCache.GetOrCreateEditorForObject(targetItem);

            Label titleLabel = targetElement.Q<Foldout>("header-foldout").Q<Label>();
            titleLabel.RegisterCallback<MouseDownEvent>(evt => { RenameItemAtIndex(targetIndex); });
            
            targetElement.RegisterCallback<MouseUpEvent>(evt =>
            {
                if (evt.button == 1)
                {
                    ShowOptionsForIndex(targetIndex);
                }
            });

            imguiContainer.onGUIHandler = () => {
            {
                editor.OnInspectorGUI();
            } };
        }

        private void ShowOptionsForIndex(int targetIndex)
        {
            ScriptableObject scriptableObject = filteredItems[targetIndex];
            
            GenericMenu menu = new GenericMenu();

            menu.AddItem(
                new GUIContent("Copy Values"),
                false,
                () =>
                {
                    CopyCollectionItemUtility.SetSource(scriptableObject);
                }
            );
            if (CopyCollectionItemUtility.CanPasteToTarget(scriptableObject))
            {
                menu.AddItem(
                    new GUIContent("Paste Values"),
                    false,
                    () =>
                    {
                        CopyCollectionItemUtility.ApplySourceToTarget(scriptableObject);
                    }
                );
            }
            else
            {
                menu.AddDisabledItem(new GUIContent("Paste Values"));
            }
            menu.AddSeparator("");

            menu.AddItem(
                new GUIContent("Duplicate Item"),
                false,
                () =>
                {
                    DuplicateItem(targetIndex);
                }
            );
                
            menu.AddItem(
                new GUIContent("Delete Item"),
                false,
                () =>
                {
                    RemoveItemAtIndex(targetIndex);
                }
            );
                
            menu.AddSeparator("");
            menu.AddItem(
                new GUIContent("Select Asset"),
                false,
                () =>
                {
                    SelectItemAtIndex(targetIndex);
                }
            );
                
            menu.ShowAsContext();
        }
        
        private void SelectItemAtIndex(int index)
        {
            ScriptableObject collectionItem = filteredItems[index];
            Selection.objects = new Object[] { collectionItem };
        }
        
        private void DuplicateItem(int index)
        {
            ScriptableObject source = filteredItems[index];
            CopyCollectionItemUtility.SetSource(source);
            ScriptableObject newItem = AddNewItemOfType(source.GetType(), false);
            CopyCollectionItemUtility.ApplySourceToTarget(newItem);
            int targetIndex = filteredItems.IndexOf(newItem);
            RenameItemAtIndex(targetIndex);
        }

        private void RenameItemAtIndex(int targetIndex)
        {
            ClearCurrentRenamingItem();

            Undo.RecordObject(filteredItems[targetIndex], "Rename Item");
            VisualElement targetElement = collectionItemListView.GetRootElementForIndex(targetIndex);

            currentRenamingLabel = targetElement.Q<Foldout>().Q<Toggle>().Q<Label>();

            currentRenamingLabel.style.display = DisplayStyle.None;

            currentRenamingTextField = targetElement.Q<TextField>();
            currentRenamingTextField.RegisterCallback<FocusOutEvent>(OnRenamingAssetLostFocus);
            currentRenamingTextField.RegisterValueChangedCallback(_ => OnFinishRenamingItem(targetIndex));

            currentRenamingTextField.SetValueWithoutNotify(currentRenamingLabel.text);
            currentRenamingTextField.style.display = DisplayStyle.Flex;
            currentRenamingTextField.SelectAll();
            currentRenamingTextField.Focus();
            collectionItemListView.ClearSelection();
        }

        private void OnRenamingAssetLostFocus(FocusOutEvent evt)
        {
            ClearCurrentRenamingItem();
        }

        private void OnFinishRenamingItem(int targetIndex)
        {
            if (currentRenamingTextField == null)
                return;

            string targetNewName = currentRenamingTextField.text;
            if (targetNewName.IsReservedKeyword())
            {
                Debug.LogError($"{targetNewName} is a reserved C# keyword, will cause issues with " +
                               $"code generation, reverting to previous name");
            }
            else
            {
                ScriptableObject asset = filteredItems[targetIndex];
                Undo.RecordObject(asset, "Rename Item");
                AssetDatabaseUtils.RenameAsset(asset, targetNewName);
                AssetDatabase.SaveAssetIfDirty(asset);
            }

            ClearCurrentRenamingItem();
        }

        private void ClearCurrentRenamingItem()
        {
            if (currentRenamingTextField == null)
                return;

            currentRenamingTextField.style.display = DisplayStyle.None;
            currentRenamingLabel.style.display = DisplayStyle.Flex;
            currentRenamingLabel.text = currentRenamingTextField.text;
            currentRenamingTextField.SetValueWithoutNotify("");
            currentRenamingLabel = null;
            currentRenamingTextField = null;
        }

        private VisualElement MakeCollectionItemListItem()
        {
            TemplateContainer makeCollectionItemListItem = collectionItemVisualTreeAsset.CloneTree();
            return makeCollectionItemListItem;
        }

        
        [MenuItem("CONTEXT/ScriptableObjectCollection/Create Generator", false, 99999)]
        private static void CreateGenerator(MenuCommand command)
        {
            Type collectionType = command.context.GetType();
            
            GeneratorCreationWizard.Show(collectionType);
        }
        
        [MenuItem("CONTEXT/ScriptableObjectCollection/Create Generator", true)]
        private static bool CreateGeneratorValidator(MenuCommand command)
        {
            Type collectionType = command.context.GetType();
            return CollectionGenerators.GetGeneratorTypeForCollection(collectionType) == null;
        }
        
        [MenuItem("CONTEXT/ScriptableObjectCollection/Edit Generator", false, 99999)]
        private static void EditGenerator(MenuCommand command)
        {
            Type collectionType = command.context.GetType();
            Type generatorType = CollectionGenerators.GetGeneratorTypeForCollection(collectionType);
            
            if (ScriptUtility.TryGetScriptOfClass(generatorType, out MonoScript script))
                AssetDatabase.OpenAsset(script);
        }
        
        [MenuItem("CONTEXT/ScriptableObjectCollection/Edit Generator", true)]
        private static bool EditGeneratorValidator(MenuCommand command)
        {
            Type collectionType = command.context.GetType();
            return CollectionGenerators.GetGeneratorTypeForCollection(collectionType) != null;
        }
        
        class CollectionCustomEditorAssetPostProcessor : AssetPostprocessor
        {
            [UsedImplicitly]
            private static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets,
                string[] movedAssets, string[] movedFromAssetPaths, bool didDomainReload)
            {
                if (!didDomainReload)
                    return;

                if (!IsWaitingForNewTypeBeCreated)
                    return;

                IsWaitingForNewTypeBeCreated = false;

                string lastGeneratedCollectionScriptPath =
                    CreateNewCollectionItemFromBaseWizard.LastGeneratedCollectionScriptPath.Value;
                string lastCollectionFullName = CreateNewCollectionItemFromBaseWizard.LastCollectionFullName.Value;

                if (string.IsNullOrEmpty(lastGeneratedCollectionScriptPath))
                    return;

                CreateNewCollectionItemFromBaseWizard.LastCollectionFullName.Value = string.Empty;
                CreateNewCollectionItemFromBaseWizard.LastGeneratedCollectionScriptPath.Value = string.Empty;

                string assemblyName =
                    CompilationPipeline.GetAssemblyNameFromScriptPath(lastGeneratedCollectionScriptPath);

                Type targetType = Type.GetType($"{lastCollectionFullName}, {assemblyName}");

                if (CollectionsRegistry.Instance.TryGetCollectionFromItemType(targetType,
                        out ScriptableObjectCollection collection))
                {
                    Selection.activeObject = null;
                    LAST_ADDED_COLLECTION_ITEM = collection.AddNew(targetType);
                    Selection.activeObject = collection;
                }
            }
        }
    }
}
