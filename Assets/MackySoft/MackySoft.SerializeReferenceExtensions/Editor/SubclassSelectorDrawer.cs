#if UNITY_2019_3_OR_NEWER
using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.IMGUI.Controls;

namespace MackySoft.SerializeReferenceExtensions.Editor {

	[CustomPropertyDrawer(typeof(SubclassSelectorAttribute))]
	public class SubclassSelectorDrawer : PropertyDrawer {

		struct TypePopupCache {
			public AdvancedTypePopup TypePopup { get; }
			public AdvancedDropdownState State { get; }
			public TypePopupCache (AdvancedTypePopup typePopup,AdvancedDropdownState state) {
				TypePopup = typePopup;
				State = state;
			}
		}

		const int k_MaxTypePopupLineCount = 13;
		static readonly Type k_UnityObjectType = typeof(UnityEngine.Object);
		static readonly GUIContent k_NullDisplayName = new GUIContent(TypeMenuUtility.k_NullDisplayName);
		static readonly GUIContent k_IsNotManagedReferenceLabel = new GUIContent("The property type is not manage reference.");

		readonly Dictionary<string,TypePopupCache> m_TypePopups = new Dictionary<string,TypePopupCache>();
		readonly Dictionary<string,GUIContent> m_TypeNameCaches = new Dictionary<string,GUIContent>();

		SerializedProperty m_TargetProperty;

		public override void OnGUI (Rect position,SerializedProperty property,GUIContent label) {
			EditorGUI.BeginProperty(position,label,property);

			if (property.propertyType == SerializedPropertyType.ManagedReference) {
				// Draw the subclass selector popup.
				Rect popupPosition = new Rect(position);
				popupPosition.width -= EditorGUIUtility.labelWidth;
				popupPosition.x += EditorGUIUtility.labelWidth;
				popupPosition.height = EditorGUIUtility.singleLineHeight;

				if (EditorGUI.DropdownButton(popupPosition,GetTypeName(property),FocusType.Keyboard)) {
					TypePopupCache popup = GetTypePopup(property);
					m_TargetProperty = property;
					popup.TypePopup.Show(popupPosition);
				}

				// Try to draw the managed reference property with a custom PropertyDrawer if it has one, otherwise draw default property field.
				if (property.managedReferenceValue == null || !TryDrawCustomUI(position, property, label)) {
					// Draw the managed reference property.
					EditorGUI.PropertyField(position, property, label, true); 
				} 
			} else {
				EditorGUI.LabelField(position,label,k_IsNotManagedReferenceLabel);
			}

			EditorGUI.EndProperty();
		}
		
		/// <summary>
		/// If target SerializedReference type has a CustomPropertyDrawer defined, we will draw it instead of the default style.
		/// </summary>
		/// <param name="position"></param>
		/// <param name="property"></param>
		/// <param name="label"></param>
		/// <returns></returns>
		private bool TryDrawCustomUI(Rect position, SerializedProperty property, GUIContent label)
        	{
			var scriptAttrUtility = typeof(PropertyDrawer).Assembly.GetType("UnityEditor.ScriptAttributeUtility");
			//Returns the actual CustomPropertyDrawer that might exist for the actual reference, instead of the SubclassSelectorDrawer context we're in.
			var getDrawerType = scriptAttrUtility.GetMethod("GetDrawerTypeForPropertyAndType", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
			var customDrawerType = (Type)getDrawerType.Invoke(null, new object[] { property, property.managedReferenceValue.GetType() });
			//Doesn't have a CustomPropertyDrawer
			if(customDrawerType == null) { return false; }
			//Get the Property Drawer instance manager for this type
			var getPropertyHandler = scriptAttrUtility.GetMethod("GetHandler", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
			var propertyHandler = getPropertyHandler.Invoke(null, new object[] { property });

			var getDrawerList = propertyHandler.GetType().GetField("m_PropertyDrawers", BindingFlags.NonPublic | BindingFlags.Instance);
			List<PropertyDrawer> drawerList = (List<PropertyDrawer>)getDrawerList.GetValue(propertyHandler);

			var drawerCount = drawerList.Count;
			var nestingLevel = (int)propertyHandler.GetType().GetField("m_NestingLevel", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(propertyHandler);

			//If our drawerCount is the same as our nesting level, it means we haven't created an instance of our CustomPropertyDrawer yet as there are less Drawers than NestingLevels.
			if(drawerCount > 0 && drawerCount == nestingLevel)
            		{
				//Get the FieldInfo of the field that holds the SerializedReference of this.
				var parentType = property.serializedObject.targetObject.GetType();
				var targetField = parentType.GetField(property.propertyPath, BindingFlags.Instance | BindingFlags.Public | BindingFlags.Public);

				PropertyDrawer propertyDrawer = (PropertyDrawer)Activator.CreateInstance(customDrawerType);
				//Let the propertyDrawer know what field it's operating on
				propertyDrawer.GetType().GetField("m_FieldInfo", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(propertyDrawer, targetField);

				drawerList.Add(propertyDrawer);
			}

			var customDrawer = drawerList[nestingLevel];
			//Finally trigger our CustomPropertyDrawer to draw itself.
			customDrawer.OnGUI(position, property, label);

			return true;
        	}

		TypePopupCache GetTypePopup (SerializedProperty property) {
			// Cache this string. This property internally call Assembly.GetName, which result in a large allocation.
			string managedReferenceFieldTypename = property.managedReferenceFieldTypename;

			if (!m_TypePopups.TryGetValue(managedReferenceFieldTypename,out TypePopupCache result)) {
				var state = new AdvancedDropdownState();
				
				Type baseType = ManagedReferenceUtility.GetType(managedReferenceFieldTypename);
				var popup = new AdvancedTypePopup(
					TypeCache.GetTypesDerivedFrom(baseType).Append(baseType).Where(p =>
						(p.IsPublic || p.IsNestedPublic) &&
						!p.IsAbstract &&
						!p.IsGenericType &&
						!k_UnityObjectType.IsAssignableFrom(p) &&
						Attribute.IsDefined(p,typeof(SerializableAttribute))
					),
					k_MaxTypePopupLineCount,
					state
				);
				popup.OnItemSelected += item => {
					Type type = item.Type;
					object obj = m_TargetProperty.SetManagedReference(type);
					m_TargetProperty.isExpanded = (obj != null);
					m_TargetProperty.serializedObject.ApplyModifiedProperties();
					m_TargetProperty.serializedObject.Update();
				};

				result = new TypePopupCache(popup, state);
				m_TypePopups.Add(managedReferenceFieldTypename, result);
			}
			return result;
		}

		GUIContent GetTypeName (SerializedProperty property) {
			// Cache this string.
			string managedReferenceFullTypename = property.managedReferenceFullTypename;

			if (string.IsNullOrEmpty(managedReferenceFullTypename)) {
				return k_NullDisplayName;
			}
			if (m_TypeNameCaches.TryGetValue(managedReferenceFullTypename,out GUIContent cachedTypeName)) {
				return cachedTypeName;
			}

			Type type = ManagedReferenceUtility.GetType(managedReferenceFullTypename);
			string typeName = null;

			AddTypeMenuAttribute typeMenu = TypeMenuUtility.GetAttribute(type);
			if (typeMenu != null) {
				typeName = typeMenu.GetTypeNameWithoutPath();
				if (!string.IsNullOrWhiteSpace(typeName)) {
					typeName = ObjectNames.NicifyVariableName(typeName);
				}
			}

			if (string.IsNullOrWhiteSpace(typeName)) {
				typeName = ObjectNames.NicifyVariableName(type.Name);
			}

			GUIContent result = new GUIContent(typeName);
			m_TypeNameCaches.Add(managedReferenceFullTypename,result);
			return result;
		}

		public override float GetPropertyHeight (SerializedProperty property,GUIContent label) {
			return EditorGUI.GetPropertyHeight(property,true);
		}

	}
}
#endif
