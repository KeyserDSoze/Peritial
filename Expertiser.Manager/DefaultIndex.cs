using Microsoft.Azure.Amqp.Framing;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Rystem.Utility;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace Expertiser
{
    public class DefaultIndex<T>
        where T : class
    {
        public List<string> DeleterRoles { get; }
        public List<string> EditorRoles { get; }
        public DefaultIndex()
        {
        }
        public List<Property> Properties { get; } = new List<Property>();
        public DefaultIndexProperty<T, TProperty> Include<TProperty>(Expression<Func<T, TProperty>> navigationPropertyPath)
        {
            dynamic body = navigationPropertyPath.Body;
            this.Properties.Add(new Property(body.Member));
            this.Last = this.Properties.Last();
            return new DefaultIndexProperty<T, TProperty>(this);
        }
        public DefaultIndexProperty<T, TProperty> Include<TProperty>(Expression<Func<T, IEnumerable<TProperty>>> navigationPropertyPath)
        {
            dynamic body = navigationPropertyPath.Body;
            this.Properties.Add(new Property(body.Member));
            this.Last = this.Properties.Last();
            return new DefaultIndexProperty<T, TProperty>(this);
        }
        private Property Last;
        internal DefaultIndexProperty<T, TNewProperty> SetSon<TNewProperty>(PropertyInfo propertyInfo)
        {
            this.Last.Properties.Add(new Property(propertyInfo, this.Last));
            this.Last = this.Last.Properties.Last();
            return new DefaultIndexProperty<T, TNewProperty>(this);
        }
    }
    public class Property
    {
        public PropertyInfo PropertyInfo { get; }
        public List<Property> Properties { get; }
        public Property Father { get; }
        public bool HasFather { get; }
        public Property(PropertyInfo propertyInfo, Property father = null)
        {
            this.PropertyInfo = propertyInfo;
            this.Properties = new List<Property>();
            if (this.HasFather = father != null)
                this.Father = father;
        }
        public IEnumerable<Property> GetAllProperties()
        {
            if (this.Properties.Count == 0)
                yield return this;
            else
                foreach (Property property in this.Properties)
                    foreach (Property pi in property.GetAllProperties())
                        yield return pi;
        }
        public string FromObject(object entity)
        {
            Property father = this.Father;
            Stack<Property> tree = new Stack<Property>();
            tree.Push(this);
            while (father != null)
            {
                tree.Push(father);
                father = father.Father;
            }
            List<Wrapper> result = new List<Wrapper>() { new Wrapper() { Value = entity } };
            while (tree.Count > 0)
            {
                Property property = tree.Pop();
                List<Wrapper> innerResult = new List<Wrapper>();
                if (!Primitive.Is(property.PropertyInfo.PropertyType) && typeof(IEnumerable).IsAssignableFrom(property.PropertyInfo.PropertyType))
                {
                    foreach (Wrapper wrapper in result)
                        foreach (object value in property.PropertyInfo.GetValue(wrapper.Value) as IEnumerable)
                            innerResult.Add(new Wrapper() { Value = value });
                    result = innerResult;
                }
                else
                {
                    foreach (Wrapper wrapper in result)
                        wrapper.Value = property.PropertyInfo.GetValue(wrapper.Value);
                }
            }
            return string.Join(",", result);
        }
    }
    public class Wrapper
    {
        public object Value { get; set; }
        public override string ToString()
            => Value?.ToString();
    }
    public class DefaultIndexProperty<T, TPreviousProperty>
        where T : class
    {
        public DefaultIndex<T> DefaultIndex { get; }
        public DefaultIndexProperty(DefaultIndex<T> defaultIndex)
        {
            this.DefaultIndex = defaultIndex;
        }
        public DefaultIndexProperty<T, TProperty> ThenInclude<TProperty>(Expression<Func<TPreviousProperty, TProperty>> navigationPropertyPath)
        {
            dynamic body = navigationPropertyPath.Body;
            return this.DefaultIndex.SetSon<TProperty>(body.Member);
        }
        public DefaultIndexProperty<T, TProperty> ThenInclude<TProperty>(Expression<Func<TPreviousProperty, IEnumerable<TProperty>>> navigationPropertyPath)
        {
            dynamic body = navigationPropertyPath.Body;
            return this.DefaultIndex.SetSon<TProperty>(body.Member);
        }
        public DefaultIndexProperty<T, TProperty> Include<TProperty>(Expression<Func<T, TProperty>> navigationPropertyPath)
            => this.DefaultIndex.Include(navigationPropertyPath);
        public DefaultIndexProperty<T, TProperty> Include<TProperty>(Expression<Func<T, IEnumerable<TProperty>>> navigationPropertyPath)
            => this.DefaultIndex.Include(navigationPropertyPath);
        private IEnumerable<Property> GetAllProperties()
        {
            foreach (var property in this.DefaultIndex.Properties)
                foreach (var t in property.GetAllProperties())
                    yield return t;
        }
        public IEnumerable<string> Headers()
        {
            foreach (Property property in GetAllProperties())
                yield return property.PropertyInfo.Name;
        }
        public IEnumerable<string> Values(T entity)
        {
            foreach (Property property in GetAllProperties())
                yield return property.FromObject(entity);
        }
    }
}
